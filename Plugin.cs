using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using ECommons;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace DyeSearch;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string DyeAddonName = "ColorantColoring";
    private const float WindowWidth = 350f;
    private const float Gap = 6f;
    private const int SelectionTimeoutMs = 6000;

    private static readonly TimeSpan InitialSelectionDelay = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan UiPollDelay = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan CategorySettleDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan ColorClickDelay = TimeSpan.FromMilliseconds(60);

    private readonly List<DyeEntry> dyes = [];
    private readonly List<byte> shadeOrder = [];
    private readonly Dictionary<byte, List<DyeEntry>> dyesByShade = [];

    private string search = string.Empty;
    private string selectionStatus = string.Empty;
    private int selectionGeneration;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        ECommonsMain.Init(pluginInterface, this);
        LoadDyes();
        Svc.PluginInterface.UiBuilder.Draw += Draw;
    }

    public void Dispose()
    {
        selectionGeneration++;
        Svc.PluginInterface.UiBuilder.Draw -= Draw;
        ECommonsMain.Dispose();
    }

    private void LoadDyes()
    {
        dyes.Clear();
        shadeOrder.Clear();
        dyesByShade.Clear();

        var sheet = Svc.Data.GetExcelSheet<Stain>();
        foreach (var stain in sheet)
        {
            if (stain.RowId == 0 || stain.RowId > byte.MaxValue)
                continue;

            var name = stain.Name.ToString();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            dyes.Add(new DyeEntry(
                (byte)stain.RowId,
                name,
                ToImGuiColor(stain.Color),
                (byte)stain.Shade,
                (byte)stain.SubOrder));
        }

        dyes.Sort(static (a, b) => a.StainId.CompareTo(b.StainId));

        foreach (var group in dyes
                     .GroupBy(x => x.Shade)
                     .OrderBy(x => x.Key))
        {
            shadeOrder.Add(group.Key);
            dyesByShade[group.Key] = group
                .OrderBy(x => x.SubOrder)
                .ThenBy(x => x.StainId)
                .ToList();
        }
    }

    private void Draw()
    {
        var addon = Svc.GameGui.GetAddonByName(DyeAddonName);
        if (addon.IsNull || !addon.IsReady || !addon.IsVisible)
            return;

        var viewport = ImGui.GetMainViewport();
        var viewportPos = viewport.Pos;
        var viewportSize = viewport.Size;

        var height = Math.Clamp(
            addon.ScaledHeight,
            300f,
            Math.Max(300f, viewportSize.Y - 20f));

        var x = addon.X + addon.ScaledWidth + Gap;
        if (x + WindowWidth > viewportPos.X + viewportSize.X)
            x = addon.X - WindowWidth - Gap;

        x = Math.Clamp(
            x,
            viewportPos.X,
            Math.Max(viewportPos.X, viewportPos.X + viewportSize.X - WindowWidth));

        var y = Math.Clamp(
            (float)addon.Y,
            viewportPos.Y,
            Math.Max(viewportPos.Y, viewportPos.Y + viewportSize.Y - height));

        ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(WindowWidth, height), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.96f);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoFocusOnAppearing;

        if (!ImGui.Begin("染剂搜索##DyeSearchDock", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint(
            "##DyeSearchInput",
            "搜索染剂名称或染色ID",
            ref search,
            128);

        if (!string.IsNullOrEmpty(selectionStatus))
            ImGui.TextDisabled(selectionStatus);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var selectedStain = GetSelectedStain();
        var normalizedSearch = search.Trim();

        if (ImGui.BeginChild("##DyeSearchList", Vector2.Zero, false))
        {
            foreach (var dye in dyes)
            {
                if (!Matches(dye, normalizedSearch))
                    continue;

                ImGui.PushID(dye.StainId);

                ImGui.ColorButton(
                    "##color",
                    dye.Color,
                    ImGuiColorEditFlags.NoTooltip,
                    new Vector2(28f, 28f));

                ImGui.SameLine();

                var selected = selectedStain == dye.StainId;
                var label = $"{dye.Name}  [{dye.StainId}]";

                if (ImGui.Selectable(
                        label,
                        selected,
                        ImGuiSelectableFlags.None,
                        new Vector2(0f, 28f)))
                {
                    QueueSelectStain(dye);
                }

                if (ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    ImGui.Text($"Stain ID: {dye.StainId}");
                    ImGui.Text($"RGB: #{dye.Rgb:X6}");
                    ImGui.Text($"Shade: {dye.Shade}");
                    ImGui.Text($"SubOrder: {dye.SubOrder}");
                    ImGui.EndTooltip();
                }

                ImGui.PopID();
            }
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private static bool Matches(DyeEntry dye, string filter)
    {
        if (filter.Length == 0)
            return true;

        if (dye.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        return dye.StainId
            .ToString()
            .Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static unsafe byte GetSelectedStain()
    {
        var agent = AgentColorant.Instance();
        return agent == null ? (byte)0 : agent->CharaView.SelectedStain;
    }

    private void QueueSelectStain(DyeEntry dye)
    {
        var generation = ++selectionGeneration;

        if (GetSelectedStain() == dye.StainId)
        {
            selectionStatus = $"已选择 {dye.Name}";
            return;
        }

        selectionStatus = $"正在定位 {dye.Name}";
        var deadline = Environment.TickCount64 + SelectionTimeoutMs;

        Schedule(
            () => TrySelectCategory(dye, generation, deadline),
            InitialSelectionDelay);
    }

    private void TrySelectCategory(DyeEntry dye, int generation, long deadline)
    {
        if (!IsSelectionActive(generation))
            return;

        if (HasTimedOut(deadline))
        {
            selectionStatus = $"选择失败：定位 {dye.Name} 超时";
            return;
        }

        var addon = GetDyeAddon();
        if (addon == null)
        {
            Retry(
                () => TrySelectCategory(dye, generation, deadline),
                "等待染色面板");
            return;
        }

        var categoryIndex = shadeOrder.IndexOf(dye.Shade);
        if (categoryIndex < 0)
        {
            selectionStatus = $"无法确定 {dye.Name} 的颜色分类";
            return;
        }

        var categoryButtons = FindCategoryButtons(addon);
        if (categoryButtons.Count <= categoryIndex)
        {
            Retry(
                () => TrySelectCategory(dye, generation, deadline),
                $"等待颜色分类按钮 {categoryButtons.Count}/{shadeOrder.Count}");
            return;
        }

        var categoryButton = categoryButtons[categoryIndex];
        if (!IsNativeButtonEnabled(categoryButton))
        {
            Retry(
                () => TrySelectCategory(dye, generation, deadline),
                "等待颜色分类按钮可用");
            return;
        }

        if (IsNativeButtonChecked(categoryButton))
        {
            // 目标颜色已经位于当前分类时，不触发分类切换。
            // 直接等待现有颜色网格稳定后再点击目标颜色。
            selectionStatus = $"当前分类已就绪，正在定位 {dye.Name}";
            Schedule(
                () => WaitForColorGrid(dye, generation, deadline, 0, 0),
                UiPollDelay);
            return;
        }

        ClickNativeButton(addon, categoryButton);
        addon->Focus();
        selectionStatus = $"正在切换 {dye.Name} 所属分类";

        // 分类按钮的选中状态通常先变化，颜色网格随后才会重建。
        // 这里先等待分类本身真正进入选中状态。
        Schedule(
            () => WaitForCategorySelected(dye, generation, deadline, categoryIndex),
            UiPollDelay);
    }

    private void WaitForCategorySelected(
        DyeEntry dye,
        int generation,
        long deadline,
        int categoryIndex)
    {
        if (!IsSelectionActive(generation))
            return;

        if (HasTimedOut(deadline))
        {
            selectionStatus = $"选择失败：切换 {dye.Name} 所属分类超时";
            return;
        }

        var addon = GetDyeAddon();
        if (addon == null)
        {
            Retry(
                () => WaitForCategorySelected(dye, generation, deadline, categoryIndex),
                "等待染色面板");
            return;
        }

        var categoryButtons = FindCategoryButtons(addon);
        if (categoryButtons.Count <= categoryIndex)
        {
            Retry(
                () => WaitForCategorySelected(dye, generation, deadline, categoryIndex),
                "等待分类切换完成");
            return;
        }

        if (!IsNativeButtonChecked(categoryButtons[categoryIndex]))
        {
            Retry(
                () => WaitForCategorySelected(dye, generation, deadline, categoryIndex),
                "等待分类切换完成");
            return;
        }

        selectionStatus = $"分类已切换，等待颜色面板刷新";

        // 使用真实时间等待，避免高帧率下两三个 tick 只有十几毫秒。
        Schedule(
            () => WaitForColorGrid(dye, generation, deadline, 0, 0),
            CategorySettleDelay);
    }

    private void WaitForColorGrid(
        DyeEntry dye,
        int generation,
        long deadline,
        ulong previousSignature,
        int stableSamples)
    {
        if (!IsSelectionActive(generation))
            return;

        if (HasTimedOut(deadline))
        {
            selectionStatus = $"选择失败：等待 {dye.Name} 的颜色列表超时";
            return;
        }

        var addon = GetDyeAddon();
        if (addon == null)
        {
            Retry(
                () => WaitForColorGrid(dye, generation, deadline, previousSignature, stableSamples),
                "等待染色面板");
            return;
        }

        if (!TryGetColorIndex(dye, out var colorIndex))
        {
            selectionStatus = $"无法确定 {dye.Name} 在分类中的位置";
            return;
        }

        var colorList = FindColorList(addon, dye.Shade, colorIndex);
        if (colorList == null)
        {
            var lists = CollectLists(addon);
            selectionStatus = lists.Count == 0
                ? "等待原生颜色列表"
                : $"等待原生颜色列表，已发现 {lists.Count} 个 List";
            Schedule(
                () => WaitForColorGrid(dye, generation, deadline, 0, 0),
                UiPollDelay);
            return;
        }

        var info = colorList.Value;
        if (info.ListLength <= colorIndex)
        {
            selectionStatus = $"等待颜色列表项目 {info.ListLength}/{colorIndex + 1}";
            Schedule(
                () => WaitForColorGrid(dye, generation, deadline, 0, 0),
                UiPollDelay);
            return;
        }

        var signature = ComputeListSignature(info);
        var nextStableSamples = signature == previousSignature
            ? stableSamples + 1
            : 0;

        // UpdatePending/ScrollRefreshPending 在部分原生列表上可能长期保持，
        // 因此只把它们作为诊断信息，不再作为阻塞条件。
        if (nextStableSamples < 1)
        {
            selectionStatus = $"颜色列表正在稳定 {dye.Name}";
            Schedule(
                () => WaitForColorGrid(
                    dye,
                    generation,
                    deadline,
                    signature,
                    nextStableSamples),
                UiPollDelay);
            return;
        }

        SelectTargetColor(dye, generation, deadline, colorIndex);
    }

    private void SelectTargetColor(
        DyeEntry dye,
        int generation,
        long deadline,
        int colorIndex)
    {
        if (!IsSelectionActive(generation))
            return;

        if (HasTimedOut(deadline))
        {
            selectionStatus = $"选择失败：选择 {dye.Name} 超时";
            return;
        }

        var addon = GetDyeAddon();
        if (addon == null)
        {
            Retry(
                () => SelectTargetColor(dye, generation, deadline, colorIndex),
                "等待染色面板");
            return;
        }

        var colorList = FindColorList(addon, dye.Shade, colorIndex);
        if (colorList == null)
        {
            selectionStatus = $"颜色列表发生变化，重新定位 {dye.Name}";
            Schedule(
                () => WaitForColorGrid(dye, generation, deadline, 0, 0),
                UiPollDelay);
            return;
        }

        var info = colorList.Value;
        var list = (AtkComponentList*)info.ListAddress;
        if (list == null || list->ListLength <= colorIndex)
        {
            selectionStatus = $"等待 {dye.Name} 对应的列表项目";
            Schedule(
                () => WaitForColorGrid(dye, generation, deadline, 0, 0),
                UiPollDelay);
            return;
        }

        if (list->GetItemDisabledState(colorIndex))
        {
            selectionStatus = $"目标颜色当前不可选择 {dye.Name}";
            return;
        }

        // SelectItem(dispatchEvent: true) 只会触发 ListItemSelect。
        // 对染色面板来说，这一步主要负责把列表焦点和高亮移动到目标项。
        // 真正等价于鼠标点击颜色格的事件是 ListItemClick。
        // 先选中，稍等一个很短的真实时间，再由 AtkComponentList 自己
        // 通过 DispatchItemEvent 构造完整事件数据并派发点击事件。
        // 全程不手工调用 AtkEventListener.ReceiveEvent。
        list->SelectItem(colorIndex, dispatchEvent: true);

        selectionStatus = $"已定位 {dye.Name}，正在触发原生点击";

        Schedule(
            () => DispatchTargetColorClick(
                dye,
                generation,
                deadline,
                colorIndex),
            ColorClickDelay);
    }

    private void DispatchTargetColorClick(
        DyeEntry dye,
        int generation,
        long deadline,
        int colorIndex)
    {
        if (!IsSelectionActive(generation))
            return;

        if (HasTimedOut(deadline))
        {
            selectionStatus = $"选择失败：触发 {dye.Name} 点击超时";
            return;
        }

        var addon = GetDyeAddon();
        if (addon == null)
        {
            Retry(
                () => DispatchTargetColorClick(
                    dye,
                    generation,
                    deadline,
                    colorIndex),
                "等待染色面板");
            return;
        }

        var colorList = FindColorList(addon, dye.Shade, colorIndex);
        if (colorList == null)
        {
            selectionStatus = $"颜色列表发生变化，重新定位 {dye.Name}";
            Schedule(
                () => WaitForColorGrid(dye, generation, deadline, 0, 0),
                UiPollDelay);
            return;
        }

        var info = colorList.Value;
        var list = (AtkComponentList*)info.ListAddress;
        if (list == null || list->ListLength <= colorIndex)
        {
            selectionStatus = $"等待 {dye.Name} 对应的列表项目";
            Schedule(
                () => WaitForColorGrid(dye, generation, deadline, 0, 0),
                UiPollDelay);
            return;
        }

        if (list->GetItemDisabledState(colorIndex))
        {
            selectionStatus = $"目标颜色当前不可选择 {dye.Name}";
            return;
        }

        // 如果短暂刷新让 SelectItem 的高亮丢失，再同步一次索引。
        // 不需要再次派发 ListItemSelect，因为真正的业务回调来自下方 ListItemClick。
        if (list->SelectedItemIndex != colorIndex)
            list->SelectItem(colorIndex, dispatchEvent: false);

        // DispatchItemEvent 是 AtkComponentList 自身提供的原生入口。
        // 它会为指定列表项构造正确的事件上下文，避免 v0.4 中
        // 手工调用 Listener.ReceiveEvent 导致的无效事件数据与崩溃。
        list->DispatchItemEvent(colorIndex, AtkEventType.ListItemClick);

        // 到这里已经完成两件事：
        // 1. SelectItem 将目标颜色格移动到原生列表选中/高亮状态。
        // 2. DispatchItemEvent(ListItemClick) 将真实的颜色格点击交给原生列表处理。
        // 不再读取 AgentColorant.SelectedStain 做二次确认，也不再安排任何重试。
        // 染色面板后续如何更新完全交给游戏自身。
        selectionStatus = $"已选择 {dye.Name}";
    }

    private bool TryGetColorIndex(DyeEntry dye, out int colorIndex)
    {
        colorIndex = -1;

        if (!dyesByShade.TryGetValue(dye.Shade, out var shadeDyes))
            return false;

        colorIndex = shadeDyes.FindIndex(x => x.StainId == dye.StainId);
        return colorIndex >= 0;
    }

    private static ulong ComputeListSignature(NativeList list)
    {
        var hash = new HashCode();
        hash.Add(list.ListAddress);
        hash.Add(list.NodeId);
        hash.Add(list.ListLength);
        hash.Add(list.SelectedItemIndex);
        hash.Add(list.ColumnCount);
        hash.Add(list.NumVisibleColumns);
        hash.Add(list.NumVisibleRows);
        hash.Add(list.NumVisibleItems);
        hash.Add(list.ItemWidth);
        hash.Add(list.ItemHeight);
        hash.Add((int)MathF.Round(list.X));
        hash.Add((int)MathF.Round(list.Y));
        hash.Add((int)MathF.Round(list.Width));
        hash.Add((int)MathF.Round(list.Height));
        return unchecked((ulong)hash.ToHashCode());
    }

    private bool IsSelectionActive(int generation)
        => generation == selectionGeneration;

    private static bool HasTimedOut(long deadline)
        => Environment.TickCount64 >= deadline;

    private void Retry(System.Action action, string status)
    {
        selectionStatus = status;
        Schedule(action, UiPollDelay);
    }

    private static void Schedule(System.Action action, TimeSpan delay)
    {
        _ = Svc.Framework.RunOnTick(action, delay: delay);
    }

    private static unsafe AtkUnitBase* GetDyeAddon()
    {
        var addon = Svc.GameGui.GetAddonByName(DyeAddonName);
        if (addon.IsNull || !addon.IsReady || !addon.IsVisible)
            return null;

        return (AtkUnitBase*)addon.Address;
    }


    private static bool IsNativeButtonEnabled(NativeButton info)
    {
        if (info.ButtonAddress == 0)
            return false;

        if (info.Type == ComponentType.RadioButton)
        {
            var radio = (AtkComponentRadioButton*)info.ButtonAddress;
            return radio->AtkComponentButton.IsEnabled;
        }

        var button = (AtkComponentButton*)info.ButtonAddress;
        return button->IsEnabled;
    }

    private static bool IsNativeButtonChecked(NativeButton info)
    {
        if (info.ButtonAddress == 0)
            return false;

        if (info.Type == ComponentType.RadioButton)
        {
            var radio = (AtkComponentRadioButton*)info.ButtonAddress;
            return radio->IsSelected;
        }

        var button = (AtkComponentButton*)info.ButtonAddress;
        return button->IsChecked;
    }

    private static void ClickNativeButton(AtkUnitBase* addon, NativeButton info)
    {
        if (info.ButtonAddress == 0)
            return;

        if (info.Type == ComponentType.RadioButton)
        {
            var radio = (AtkComponentRadioButton*)info.ButtonAddress;
            radio->ClickRadioButton(addon);
            return;
        }

        var button = (AtkComponentButton*)info.ButtonAddress;
        button->ClickAddonButton(addon);
    }

    private List<NativeButton> FindCategoryButtons(AtkUnitBase* addon)
    {
        // 分类按钮在 ColorantColoring 的主 ULD 上。这里只扫描顶层，
        // 避免递归后把颜色网格中的 RadioButton 错认成分类。
        var buttons = CollectTopLevelButtons(addon);
        var expectedCount = shadeOrder.Count;

        var preferred = buttons
            .Where(x => x.Type == ComponentType.RadioButton && x.HasEllipticalCollision)
            .ToList();

        var row = FindBestHorizontalRow(preferred, expectedCount);
        if (row.Count >= expectedCount)
            return row;

        var fallback = buttons
            .Where(x => x.Type == ComponentType.RadioButton)
            .ToList();

        return FindBestHorizontalRow(fallback, expectedCount);
    }

    private static List<NativeButton> FindBestHorizontalRow(
        IReadOnlyList<NativeButton> buttons,
        int expectedCount)
    {
        if (expectedCount <= 0 || buttons.Count < expectedCount)
            return [];

        List<NativeButton>? best = null;
        var bestScore = float.MaxValue;

        foreach (var anchor in buttons)
        {
            var yTolerance = Math.Max(4f, anchor.Height * 0.35f);
            var sizeTolerance = Math.Max(4f, Math.Max(anchor.Width, anchor.Height) * 0.25f);

            var row = buttons
                .Where(x =>
                    Math.Abs(x.Y - anchor.Y) <= yTolerance &&
                    Math.Abs(x.Width - anchor.Width) <= sizeTolerance &&
                    Math.Abs(x.Height - anchor.Height) <= sizeTolerance)
                .OrderBy(x => x.X)
                .ToList();

            if (row.Count < expectedCount)
                continue;

            for (var start = 0; start <= row.Count - expectedCount; start++)
            {
                var candidate = row
                    .Skip(start)
                    .Take(expectedCount)
                    .ToList();

                var score = HorizontalRowScore(candidate);
                if (score >= bestScore)
                    continue;

                bestScore = score;
                best = candidate;
            }
        }

        return best ?? [];
    }

    private static float HorizontalRowScore(IReadOnlyList<NativeButton> row)
    {
        if (row.Count <= 1)
            return 0f;

        var averageY = row.Average(x => x.Y);
        var yVariance = row.Sum(x => MathF.Abs(x.Y - averageY));

        var gaps = new List<float>(row.Count - 1);
        for (var i = 1; i < row.Count; i++)
            gaps.Add(row[i].X - row[i - 1].X);

        var averageGap = gaps.Average();
        var gapVariance = gaps.Sum(x => MathF.Abs(x - averageGap));

        var averageWidth = row.Average(x => x.Width);
        var averageHeight = row.Average(x => x.Height);
        var sizeVariance = row.Sum(x =>
            MathF.Abs(x.Width - averageWidth) +
            MathF.Abs(x.Height - averageHeight));

        return yVariance + gapVariance + sizeVariance;
    }

    private NativeList? FindColorList(AtkUnitBase* addon, byte shade, int colorIndex)
    {
        var lists = CollectLists(addon);
        if (lists.Count == 0)
            return null;

        dyesByShade.TryGetValue(shade, out var expectedDyes);
        var expectedCount = expectedDyes?.Count ?? 0;

        var categoryButtons = FindCategoryButtons(addon);
        var categoryBottom = categoryButtons.Count == 0
            ? float.MinValue
            : categoryButtons.Max(x => x.Y + x.Height);

        NativeList? best = null;
        var bestScore = double.MaxValue;

        foreach (var list in lists)
        {
            if (list.ListLength <= colorIndex || list.ListLength <= 0)
                continue;

            // 染色颜色区域是多列网格。多列属性越明显，优先级越高。
            var gridLike =
                list.ColumnCount > 1 ||
                list.NumVisibleColumns > 1 ||
                list.ColumnStepX != 0;

            var countPenalty = expectedCount > 0
                ? Math.Abs(list.ListLength - expectedCount) * 500.0
                : 0.0;

            var gridPenalty = gridLike ? 0.0 : 1500.0;
            var positionPenalty = categoryButtons.Count > 0 && list.Y < categoryBottom - 4f
                ? 1000.0
                : 0.0;

            var visibilityPenalty = list.NumVisibleItems <= 0 ? 500.0 : 0.0;
            var interactionPenalty = list.IsItemInteractionEnabled ? 0.0 : 250.0;
            var sizePenalty = list.Width < 40f || list.Height < 30f ? 500.0 : 0.0;

            // 精确匹配当前 Shade 的 Stain 数量时强烈优先。
            var exactCountBonus = expectedCount > 0 && list.ListLength == expectedCount
                ? -2000.0
                : 0.0;

            var score =
                countPenalty +
                gridPenalty +
                positionPenalty +
                visibilityPenalty +
                interactionPenalty +
                sizePenalty +
                exactCountBonus;

            if (score >= bestScore)
                continue;

            bestScore = score;
            best = list;
        }

        return best;
    }

    private static unsafe List<NativeList> CollectLists(AtkUnitBase* addon)
    {
        var result = new List<NativeList>();
        var visitedManagers = new HashSet<nint>();
        var visitedLists = new HashSet<nint>();
        var addonScale = addon->Scale <= 0f ? 1f : addon->Scale;

        CollectListsRecursive(
            &addon->UldManager,
            addonScale,
            result,
            visitedManagers,
            visitedLists);

        return result;
    }

    private static unsafe void CollectListsRecursive(
        AtkUldManager* manager,
        float addonScale,
        List<NativeList> result,
        HashSet<nint> visitedManagers,
        HashSet<nint> visitedLists)
    {
        if (manager == null || manager->NodeList == null || manager->NodeListCount == 0)
            return;

        if (!visitedManagers.Add((nint)manager))
            return;

        for (var i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || !node->IsVisible() || node->GetNodeType() != NodeType.Component)
                continue;

            var component = node->GetComponent();
            if (component == null)
                continue;

            var type = component->GetComponentType();
            if (type == ComponentType.List || type == ComponentType.TreeList)
            {
                var listAddress = (nint)component;
                if (visitedLists.Add(listAddress))
                {
                    var list = (AtkComponentList*)component;
                    var width = MathF.Abs(node->Width * node->ScaleX * addonScale);
                    var height = MathF.Abs(node->Height * node->ScaleY * addonScale);

                    result.Add(new NativeList(
                        listAddress,
                        (nint)node,
                        node->NodeId,
                        node->ScreenX,
                        node->ScreenY,
                        width,
                        height,
                        list->ListLength,
                        list->SelectedItemIndex,
                        list->ColumnCount,
                        list->ColumnStepX,
                        list->ColumnStepY,
                        list->NumVisibleColumns,
                        list->NumVisibleRows,
                        list->NumVisibleItems,
                        list->ItemWidth,
                        list->ItemHeight,
                        list->IsItemInteractionEnabled,
                        list->IsItemClickEnabled,
                        list->IsUpdatePending,
                        list->IsScrollRefreshPending));
                }
            }

            // List 本身和其他容器组件都可能再包含子组件。
            CollectListsRecursive(
                &component->UldManager,
                addonScale,
                result,
                visitedManagers,
                visitedLists);
        }
    }

    private static unsafe List<NativeButton> CollectTopLevelButtons(AtkUnitBase* addon)
    {
        var result = new List<NativeButton>();
        var addonScale = addon->Scale <= 0f ? 1f : addon->Scale;

        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node == null || !node->IsVisible() || node->GetNodeType() != NodeType.Component)
                continue;

            var component = node->GetComponent();
            if (component == null)
                continue;

            var type = component->GetComponentType();
            if (type != ComponentType.Button && type != ComponentType.RadioButton)
                continue;

            var width = MathF.Abs(node->Width * node->ScaleX * addonScale);
            var height = MathF.Abs(node->Height * node->ScaleY * addonScale);
            if (width < 4f || height < 4f)
                continue;

            result.Add(new NativeButton(
                (nint)(AtkComponentButton*)component,
                (nint)node,
                node->NodeId,
                node->ScreenX,
                node->ScreenY,
                width,
                height,
                type,
                HasEllipticalCollision(component)));
        }

        return result;
    }

    private static unsafe bool HasEllipticalCollision(AtkComponentBase* component)
    {
        if (component == null)
            return false;

        for (var i = 0; i < component->UldManager.NodeListCount; i++)
        {
            var node = component->UldManager.NodeList[i];
            if (node != null && node->IsEllipticalCollision)
                return true;
        }

        return false;
    }

    private static Vector4 ToImGuiColor(uint rgb)
    {
        var r = ((rgb >> 16) & 0xFF) / 255f;
        var g = ((rgb >> 8) & 0xFF) / 255f;
        var b = (rgb & 0xFF) / 255f;
        return new Vector4(r, g, b, 1f);
    }

    private readonly record struct DyeEntry(
        byte StainId,
        string Name,
        Vector4 Color,
        byte Shade,
        byte SubOrder)
    {
        public uint Rgb =>
            ((uint)Math.Round(Color.X * 255f) << 16) |
            ((uint)Math.Round(Color.Y * 255f) << 8) |
            (uint)Math.Round(Color.Z * 255f);
    }

    private readonly record struct NativeButton(
        nint ButtonAddress,
        nint NodeAddress,
        uint NodeId,
        float X,
        float Y,
        float Width,
        float Height,
        ComponentType Type,
        bool HasEllipticalCollision)
    {
        public nint IdentityAddress => ButtonAddress != 0 ? ButtonAddress : NodeAddress;
    }

    private readonly record struct NativeList(
        nint ListAddress,
        nint NodeAddress,
        uint NodeId,
        float X,
        float Y,
        float Width,
        float Height,
        int ListLength,
        int SelectedItemIndex,
        short ColumnCount,
        short ColumnStepX,
        short ColumnStepY,
        short NumVisibleColumns,
        short NumVisibleRows,
        short NumVisibleItems,
        short ItemWidth,
        short ItemHeight,
        bool IsItemInteractionEnabled,
        bool IsItemClickEnabled,
        bool IsUpdatePending,
        bool IsScrollRefreshPending);

}
