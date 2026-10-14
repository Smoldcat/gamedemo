/// <summary>
/// 弹药等级选择器的**运行时自建 UI**（快捷栏上方一条纵列：档位徽标 + 名字 + 数量、当前项高亮）。
///
/// 与 `ThrowableSelectorUI` 共用 `SelectorListUI{T}`（2026-10-15 合并，原为两份近乎逐字的实现）；
/// 本类只负责两件不一样的事：**画布名** 与 **行文字带档位徽标**（`AmmoData.TierMark`：`Ⅰ/Ⅱ`…）。
///
/// ⚠️ 分工不变：**状态在 `AmmoSelector`**（`_selectorOpen` / `_selectorCloseAt` / `_selected`），
///    本类只按传入的 `visible / 列表 / 当前项` 画。
/// </summary>
public class AmmoSelectorUI : SelectorListUI<AmmoData>
{
    protected override string CanvasName => "弹药等级选择器（运行时）";

    protected override string RowText(AmmoData item, int count) => item.TierMark + " " + item.DisplayName + "  ×" + count;
}
