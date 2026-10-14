/// <summary>
/// 投掷物选择器的**运行时自建 UI**（快捷栏上方一条纵列：名字 + 数量、当前项高亮）。
///
/// 为什么单独成类（2026-10-12 重构）：`PlayerThrowable` 原来同时扛"状态机 + 输入 + 渲染 + 这套 UI"；
/// 把**建/显隐/重绘**搬出来，主类只管"开没开、超时、高亮第几个"。
///
/// 与 `AmmoSelectorUI` 共用 `SelectorListUI{T}`（2026-10-15 合并，行为一字未变）；
/// 本类只提供**画布名**，行文字用基类的默认格式（名字 ×数量）。
///
/// ⚠️ 分工：**状态在 `PlayerThrowable`**，本类只按传入的 `visible / 列表 / 当前项` 画 ——
///    所以"暂停时要关掉"必须由主类显式传 `visible = false`。
/// </summary>
public class ThrowableSelectorUI : SelectorListUI<ThrowableData>
{
    protected override string CanvasName => "投掷物选择器（运行时）";
}
