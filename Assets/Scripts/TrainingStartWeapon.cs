using UnityEngine;

/// <summary>
/// 训练图专用：开局自动装备一把武器。
///
/// 为什么需要它：<see cref="PlayerBackpack.Awake"/> 会把 <c>currentSlot</c> 强制清成 -1
/// （正常玩法是"地上铺枪 → 玩家去捡"），而训练图希望一进游戏手上就有枪。
///
/// ⚠️ 时序是这个脚本唯一的难点，改之前先读：
///    · <see cref="PlayerController"/> 的镜像同步（weapon ← 背包当前槽）是在**它自己的 Awake** 里跑的；
///    · 本脚本若也在 Awake 里改背包，会**晚于**那次同步 → 手上仍是空手；
///    · 所以这里放在 <c>Start</c>（全部 Awake 之后）设置，再**主动调用一次**
///      <c>LoadMirrorFromSlot()</c> 把镜像刷过来（该方法 2026-10-26 提为 `internal`，避免反射）。
/// ⚠️ 只读/写背包数据，不改任何核心规则；没有配置武器时什么都不做（空手）。
/// </summary>
[DefaultExecutionOrder(100)]
public class TrainingStartWeapon : MonoBehaviour
{
    [Tooltip("开局装备的武器（留空 = 不自动装备，保持原版「地上捡枪」流程）")]
    public WeaponData startWeapon;

    [Tooltip("开局弹匣余弹（-1 = 满弹，与 WeaponPickup 同一套语义）")]
    public int startMagAmmo = -1;

    void Start()
    {
        // 训练图没有 WaveManager（已停用）→ 它原本负责的"每局初始化"在这里补上
        GameState.Reset();
        Sfx.Init();

        var bp = GetComponent<PlayerBackpack>();
        if (bp == null || bp.slots == null || bp.slots.Length == 0) return;

        // 槽里还没有任何武器时，才把 startWeapon 放进第一个槽（不覆盖玩家已有配置）
        bool anyWeapon = false;
        for (int i = 0; i < bp.slots.Length; i++)
            if (bp.slots[i] != null && bp.slots[i].weapon != null) { anyWeapon = true; break; }

        if (!anyWeapon && startWeapon != null)
        {
            if (bp.slots[0] == null) bp.slots[0] = new PlayerBackpack.Slot();
            bp.slots[0].weapon = startWeapon;
            // -1 = 满弹（WeaponPickup 同一套语义）；这里必须落成真实数字，
            // 否则 PlayerController 会把它当成"弹匣 -1 发"，开一枪就变负数
            bp.slots[0].magAmmo = startMagAmmo < 0 ? startWeapon.magSize : startMagAmmo;
        }

        for (int i = 0; i < bp.slots.Length; i++)
        {
            if (bp.slots[i] != null && bp.slots[i].weapon != null)
            {
                bp.currentSlot = i;
                break;
            }
        }

        // PlayerController 的镜像同步在它自己的 Awake 里已经跑过一次（那时还是空手）→ 这里补一次
        var pc = GetComponent<PlayerController>();
        if (pc != null) pc.LoadMirrorFromSlot();

        // 训练图兜底：CameraOcclusion.Awake 里收集建筑时，场景对象尚未全部就绪 → 收到 0 栋。
        // 这里在 Start（全部 Awake 之后）再收一次。
        var cam = Camera.main;
        if (cam != null)
        {
            var occ = cam.GetComponent<CameraOcclusion>();
            if (occ != null) occ.CollectBuildings();
        }
    }
}
