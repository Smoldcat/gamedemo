using UnityEngine;

/// <summary>
/// 俯视角摄像机：以固定构图偏移跟随玩家，玩家始终位于画面中部；持枪时朝准星方向轻微前探。
/// 挂在 Main Camera 上，相机的初始位置和旋转就是「构图偏移」（在场景里调好即可）。
/// </summary>
public class CameraRig : MonoBehaviour
{
    public static CameraRig Instance;

    [Header("跟谁")]
    [Tooltip("要跟随的目标，一般是场景里的 Player")]
    public Transform target;

    [Header("相机参数")]
    public float followLerp = 8f;
    [Range(0f, 0.5f)] public float aimLeadRatio = 0.18f; // 镜头朝准星前探比例
    public float maxAimLead = 2.5f;                       // 前探最大距离

    Quaternion _baseRot;
    Vector3 _offset;      // 相机相对跟随点的固定偏移（高度 + 后距）
    Vector3 _basePos;     // 未叠加震动时的相机位置
    Vector3 _shake;
    float _shakeAmount;

    void Awake()
    {
        // 单例防护：出现重复实例（如场景重载的极端时序）时丢弃后出现的那个
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        _basePos = transform.position;
        _baseRot = transform.rotation;
        // 把场景里的初始位置记作「构图偏移」：之后相机位置 = 跟随点 + 这个偏移
        _offset = transform.position;

        // 容错：没手动拖 target 时自动找玩家
        if (target == null)
        {
            var pc = FindObjectOfType<PlayerController>();
            if (pc != null) target = pc.transform;
        }
    }

    public static void Shake(float amount)
    {
        if (Instance == null) return;
        Instance._shakeAmount = Mathf.Min(Instance._shakeAmount + amount, 0.5f);
    }

    void LateUpdate()
    {
        // 锁定俯视角角度
        transform.rotation = _baseRot;

        if (target == null) { transform.position = _basePos; return; }

        var pc = PlayerController.Instance;

        // 跟随点：玩家位置；持枪时朝准星方向前探一点（还原俯视射击手感）
        Vector3 t = target.position;
        if (pc != null && pc.weapon != null)
        {
            Vector3 lead = pc.AimPoint - target.position;
            lead.y = 0f;
            t += Vector3.ClampMagnitude(lead * aimLeadRatio, maxAimLead);
        }

        // 核心：相机位置 = 跟随点XZ + 固定构图偏移（高度、后距保持不变，玩家固定出现在画面中部）
        Vector3 desired = new Vector3(t.x, 0f, t.z) + _offset;
        _basePos = Vector3.Lerp(_basePos, desired, followLerp * Time.deltaTime);

        // 屏幕震动（沿屏幕平面方向抖动，不污染基准位置）
        if (_shakeAmount > 0.001f)
        {
            Vector2 r = Random.insideUnitCircle;
            _shake = (transform.right * r.x + transform.up * r.y) * _shakeAmount * 0.35f;
            _shakeAmount = Mathf.MoveTowards(_shakeAmount, 0f, Time.deltaTime * 1.2f);
        }
        else _shake = Vector3.zero;

        transform.position = _basePos + _shake;
    }
}
