using System;
using UnityEngine;

/// 生命组件：玩家和敌人通用。血量归零时触发 OnDeath，供外部处理击毁逻辑。
public class Health : MonoBehaviour
{
    public enum Team { Player, Enemy }

    public Team team = Team.Enemy;
    public float maxHp = 100f;
    public float hp;
    public bool alive = true;

    public event Action<float, Vector3> OnDamaged;  // (伤害, 来向)
    public event Action OnDeath;

    /// <summary>最近一次受伤是否为爆头（供击杀反馈判断"爆头击杀"：最后致命一击是爆头才算）。</summary>
    public bool LastDamageWasHeadshot { get; private set; }
    /// <summary>最近一次受伤是否算「被击杀」（自爆兵自爆走 `creditKill: false` → 击杀反馈不显示）。默认 true。</summary>
    public bool LastDamageCredited { get; private set; } = true;

    // 本人身上「全部部件」的渲染器 + 各自基色。
    // 为什么是数组：敌人现在有 身体 + 头 + 脸 三个部件，受击白闪 / 死亡变色必须一起作用；
    // 「只抓第一个 Renderer」的老写法会漏掉头/脸，而且依赖子物体顺序（很脆）。
    // ⚠️ 在 Awake 收集：此时血条、手里的枪模型都还没创建，不会被误收进来（它们不该跟着变色）。
    Renderer[] _renderers;
    Color[] _baseColors;
    MaterialPropertyBlock _mpb;
    float _flashUntil;

    void Awake()
    {
        hp = maxHp;
        _mpb = new MaterialPropertyBlock();
        CollectRenderers();
    }

    void CollectRenderers()
    {
        _renderers = GetComponentsInChildren<Renderer>(true);
        _baseColors = new Color[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            // 从共享材质读基色：只读不改，避免为了取颜色而复制一份材质
            var m = _renderers[i] != null ? _renderers[i].sharedMaterial : null;
            _baseColors[i] = (m != null && m.HasProperty(Effects.ColorProp)) ? m.color : Color.white;
        }
    }

    /// <summary>给所有部件统一上色（不改变记录的基色）。头部/面部会跟着一起变。</summary>
    public void SetTint(Color c)
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            Effects.SetColor(_renderers[i], _mpb, c);
    }

    /// <summary>恢复到各部件的原始基色</summary>
    public void RestoreBaseTint()
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            Effects.SetColor(_renderers[i], _mpb, _baseColors[i]);
    }

    /// <summary>清掉颜色覆盖，回到材质本身的颜色（自爆兵预警闪烁结束时用）</summary>
    public void ClearTint()
    {
        if (_renderers == null) return;
        for (int i = 0; i < _renderers.Length; i++)
            Effects.ClearColor(_renderers[i]);
    }

    /// <summary>回血（自动带上限，死亡后不可用）。外部不再直接改 hp，方便将来在回血里挂治疗特效/事件。</summary>
    public void Heal(float amount)
    {
        if (!alive) return;
        hp = Mathf.Min(maxHp, hp + amount);
    }

    /// <summary>
    /// 扣血。headshot 由调用方（Combat）传入：爆头时飘字换成醒目样式并加大字号。
    /// creditKill=false 表示"这次死亡不算被击杀"（自爆兵自爆），击杀反馈不显示它。
    /// 参数带默认值 → 现有调用点（爆炸 AOE / 近战 / 自爆 / 玩家受击）都不用改。
    /// </summary>
    public void TakeDamage(float dmg, Vector3 fromDir, bool headshot = false, bool creditKill = true)
    {
        if (!alive) return;

        // 记录本次伤害的属性（每次受伤都更新 → "最后致命一击"的属性就是它）：
        // 击杀反馈靠这两个字段决定用哪套图标、以及要不要显示（自杀 = 不显示）。
        LastDamageWasHeadshot = headshot;
        LastDamageCredited = creditKill;

        hp -= dmg;
        // 伤害飘字：爆头=醒目橙红且更大；敌人=黄、玩家=红。爆头时略微抬高，与头部位置对应
        DamageText.Spawn(transform.position + Vector3.up * (headshot ? 1.45f : 1.15f), dmg,
            headshot ? DamageText.Kind.Headshot
                     : (team == Team.Enemy ? DamageText.Kind.DamageToEnemy : DamageText.Kind.DamageToPlayer));
        OnDamaged?.Invoke(dmg, fromDir);

        // 受击白闪（用 MaterialPropertyBlock 覆盖颜色，不复制材质；所有部件一起闪）
        SetTint(Color.white);
        _flashUntil = Time.time + 0.08f;

        if (team == Team.Player) CameraRig.Shake(0.15f);

        if (hp <= 0f)
        {
            hp = 0f;
            alive = false;

            // 先撤掉颜色覆盖，否则死亡变色（EnemyAI.OnDead 用 SetTint）会被这里的白闪盖住
            ClearTint();

            OnDeath?.Invoke();
        }
    }

    void Update()
    {
        // 死亡后不再恢复颜色、不再回血（否则会和 EnemyAI 的死亡变色冲突）
        if (!alive) return;

        // 白闪结束后恢复各部件的基色
        if (_flashUntil > 0f && Time.time > _flashUntil)
        {
            RestoreBaseTint();
            _flashUntil = 0f;
        }
        // 注：已移除玩家脱战自动回血（血包改为消耗品后，回血只能靠背包血包/Q 键）
    }

    void OnDestroy()
    {
        OnDeath = null;
        OnDamaged = null;
    }
}
