# Test 3：3D 粒子人脸原型实施说明

## 文档目的与当前边界

本文件整理「Unity模型替换可行性」聊天中关于 Studio3 的讨论，作为在 `Assets/Scenes/Test 3.unity` 中实现粒子人脸的设计说明和操作清单。

目标不是继续替换实体 FBX 人头，而是让摄像头中的一张人脸在声音输入达到 **High** 时，化为带摄像头 RGB 颜色的三维粒子脸；没有输入或只有 **Low** 输入时，则保持为抽象粒子云。

Test 3 已有 `Authoritycontroller`、`AuthorityVisualController`、录音和 High/Low 声音分类组件。现已接入 `ParticleFacePrototype`：它在运行时创建 `ParticleFaceSystem`，以蓝色呼吸云团作为 Idle，High 时形成带五官起伏的程序化 3D 点云脸，Low 时短暂扰动后回聚。当前仍**未**接入摄像头采集、MediaPipe 或真人 RGB 采样；High 的暖色脸是视觉占位，而不是真人脸重建。

## 从「替换模型」到「粒子人脸」

最初的实体人头替换方案中，建议使用带 BlendShapes（形态键）和 `SkinnedMeshRenderer` 的 `.fbx`：FBX 是 Unity 的稳妥导入格式，但文件格式本身不保证能变形；若既有程序依赖顶点顺序或 BlendShape 名，换一个 FBX 也不能直接保留功能。可从 [Sketchfab](https://sketchfab.com/search?type=models&q=human%20head&features=downloadable)、[CGTrader](https://www.cgtrader.com/3d-models?keywords=human%20head)、[BlenderKit](https://www.blenderkit.com/)、[TurboSquid](https://www.turbosquid.com/Search/3D-Models/human-head) 或 [MakeHuman](https://www.makehumancommunity.org/) 寻找或制作模型。

现在选择的方案不需要一个固定的写实头模。它以观众当下的面部关键点决定粒子位置、以相机图像决定粒子颜色，因此更适合「声音赋权」主题，也自然避开了不同外部 FBX 的拓扑兼容问题。

## 视觉与交互规则

| 状态 | 触发 | 粒子行为 | 表达含义 |
| --- | --- | --- | --- |
| Idle | 无输入 / 超时 | 紧凑云团缓慢呼吸、漂浮 | 尚未受评价影响 |
| Low | 输入被接收但未达到阈值 | 短暂抖动、扩散，回到云团 | 声音被听见，但没有长期改变形态 |
| High | 声音分类达到既有阈值 | 插值至当前稳定的人脸点云，并使用相机颜色 | 声音取得足以改变虚拟人物的权重 |

Low 不能没有反应：它要让参与者看到输入已被接收，同时清楚区分「被听见」与「产生改变」。High 的目标应在一小段稳定窗口内冻结，避免每帧追踪噪声令粒子闪烁。

## 技术架构

```text
WebCamTexture ──> 图像帧 / Color32[] ──> UV 颜色采样 ──> 粒子颜色
       │
       └──> MediaPipe Face Landmarker ──> 3D landmarks / face mesh ──> 粒子目标坐标

OpenSMILE + AuthorityVoiceClassifier ──> High / Low ──> ParticleFaceController 状态机
                                                           │
                                                           └──> Unity ParticleSystem.SetParticles()
```

- 摄像头：Unity `WebCamTexture`。先以 640×480、30 FPS 起步；实际设备可能忽略请求值，必须读取 `width`、`height` 后再分配缓存。
- 人脸：MediaPipe Face Landmarker 输出人脸关键点和归一化屏幕坐标。单普通 RGB 摄像头得到的是近似的正面面部几何，不是可从任意角度观看的完整头颅扫描。
- 点云：478 个关键点只够作骨架。为获得数千粒子，应按照 Face Mesh 三角形索引，在三角形内随机重心采样；每个样本同时插值位置与 UV。
- 颜色：用每粒子的 UV 采样 `Color32[]`，采样前钳制边界；必要时做镜像校正和轻微平滑，避免肤色跳变。
- 渲染：最小可行版本使用 `ParticleSystem`（约 1,000–5,000 粒子）。达到更高数量或要加复杂噪声时迁移至 VFX Graph / `GraphicsBuffer`。

## Test 3 的建议接线

1. 打开 `Assets/Scenes/Test 3.unity`，不要删除现有 `Authoritycontroller`。它已经连接声音输入、分类和文字视觉逻辑。
2. 在 Hierarchy 创建空对象 `ParticleFaceSystem`，位置先设为摄像机正前方（应依据现有构图微调），添加 `Particle System` 与下方 `ParticleFaceController`。
3. Particle System 初始配置：Simulation Space = Local；Start Lifetime = 10000；Start Speed = 0；Start Size = 0.015；Max Particles = 3000；Emission Rate over Time = 0；Shape 关闭；Renderer 使用透明或加色 Billboard 粒子材质。
4. 创建空对象 `FaceCameraInput`，后续挂 `WebCamTexture`/MediaPipe 桥接组件。预览可以指向一个 `RawImage`，但展览画面不必显示原始相机画面。
5. 在 `AuthorityVoiceClassifier` 或一个轻量适配器中，将既有 High/Low 回调分别调用 `ParticleFaceController.SetState(FaceState.High)` / `SetState(FaceState.Low)`；不应修改分类阈值本身。
6. 首先以脚本 Inspector 的 `state` 和键盘测试验证 Idle/Low/High；确认视觉、构图和性能后才引入摄像头与 MediaPipe。

现有 `AuthorityVisualController` 的 High/Low 键默认为 H/L，High 阈值为 70。粒子控制器可以订阅同一事件，也可以由一个适配器统一转发；避免两个组件互相直接依赖私有实现。

### 当前 Test 3 实现（已接入）

`Assets/Scripts/ParticleFacePrototype.cs` 已直接挂在 `Authoritycontroller` 上，并订阅其 `HighStarted`、`LowStarted` 和 `SequenceCancelled` 事件。因此无需在 Inspector 手动拖拽引用：按 H 会形成程序化粒子脸，按 L 会触发 Low 扰动，按 I 会返回 Idle。场景保存的粒子中心为 `(0, 2.12, 0)`，与 Test 3 的现有摄像机构图对齐。运行时才创建粒子对象，避免把数千个粒子状态写入场景文件。

## 可运行的第 1 阶段原型：数学脸 + 三态粒子

将下列脚本保存为 `Assets/Scripts/ParticleFaceController.cs`，挂在 `ParticleFaceSystem`。它用椭球面近似脸形，目的是验证粒子生成、聚散和颜色插值；它不读取摄像头，也不声称是 MediaPipe 实现。

```csharp
using UnityEngine;

[RequireComponent(typeof(ParticleSystem))]
public class ParticleFaceController : MonoBehaviour
{
    public enum FaceState { Idle, Low, High }

    [Header("State")]
    public FaceState state = FaceState.Idle;
    [Min(1)] public int particleCount = 3000;
    [Min(0.01f)] public float cloudRadius = 0.35f;
    [Min(0.01f)] public float faceWidth = 1.25f;
    [Min(0.01f)] public float faceHeight = 1.65f;
    [Min(0.01f)] public float faceDepth = 0.40f;
    [Min(0.01f)] public float moveSpeed = 3f;
    [Range(0f, 1f)] public float lowJitter = 0.15f;
    public Color idleColor = new Color(0.15f, 0.75f, 1f, 1f);
    public Color highFallbackColor = Color.white;

    private ParticleSystem system;
    private ParticleSystem.Particle[] particles;
    private Vector3[] cloudPositions;
    private Vector3[] facePositions;
    private Color[] faceColors;
    private float lowStartedAt;

    private void Start()
    {
        system = GetComponent<ParticleSystem>();
        var main = system.main;
        main.maxParticles = particleCount;
        main.startLifetime = 10000f;
        main.startSpeed = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        system.emission.enabled = false;
        system.Clear();
        system.Emit(particleCount);

        particles = new ParticleSystem.Particle[particleCount];
        cloudPositions = new Vector3[particleCount];
        facePositions = new Vector3[particleCount];
        faceColors = new Color[particleCount];
        system.GetParticles(particles);

        Random.InitState(417);
        for (var i = 0; i < particleCount; i++)
        {
            cloudPositions[i] = Random.insideUnitSphere * cloudRadius;
            facePositions[i] = MakeTestFacePoint();
            faceColors[i] = highFallbackColor;
            particles[i].position = cloudPositions[i];
            particles[i].startColor = idleColor;
            particles[i].remainingLifetime = 9999f;
        }
        system.SetParticles(particles, particleCount);
    }

    public void SetState(FaceState next)
    {
        if (next == FaceState.Low && state != FaceState.Low) lowStartedAt = Time.time;
        state = next;
    }

    // MediaPipe bridge calls this after it has sampled a stable face mesh.
    public void SetFaceTargets(Vector3[] positions, Color[] colors)
    {
        if (positions == null || positions.Length != particleCount) return;
        if (colors != null && colors.Length != particleCount) return;
        for (var i = 0; i < particleCount; i++)
        {
            facePositions[i] = positions[i];
            faceColors[i] = colors == null ? highFallbackColor : colors[i];
        }
    }

    private void LateUpdate()
    {
        if (particles == null) return;
        var blend = 1f - Mathf.Exp(-moveSpeed * Time.deltaTime);
        var breathe = 1f + Mathf.Sin(Time.time * 0.8f) * 0.07f;
        for (var i = 0; i < particleCount; i++)
        {
            Vector3 target;
            Color color;
            if (state == FaceState.High)
            {
                target = facePositions[i];
                color = faceColors[i];
            }
            else
            {
                target = cloudPositions[i] * breathe;
                color = idleColor;
                if (state == FaceState.Low)
                {
                    var phase = Time.time * 8f + i * 0.618f;
                    target += new Vector3(Mathf.Sin(phase), Mathf.Cos(phase * 1.3f), Mathf.Sin(phase * 0.7f))
                        * lowJitter * Mathf.Exp(-(Time.time - lowStartedAt) * 1.4f);
                }
            }
            particles[i].position = Vector3.Lerp(particles[i].position, target, blend);
            particles[i].startColor = Color.Lerp((Color)particles[i].startColor, color, blend);
        }
        system.SetParticles(particles, particleCount);
    }

    private Vector3 MakeTestFacePoint()
    {
        // Uniform-ish ellipsoid surface distribution: a stand-in for mesh sampling only.
        var z = Random.Range(-1f, 1f);
        var a = Random.value * Mathf.PI * 2f;
        var r = Mathf.Sqrt(1f - z * z);
        var p = new Vector3(r * Mathf.Cos(a) * faceWidth, z * faceHeight, r * Mathf.Sin(a) * faceDepth);
        p.z += 0.12f * Mathf.Exp(-(p.x * p.x + p.y * p.y * 1.3f)); // modest nose bulge
        return p;
    }
}
```

## 第 2 阶段：MediaPipe、网格采样与 RGB 采样

选择与 Unity 版本匹配、可在目标平台运行的 MediaPipe Unity 插件/封装，并以该封装的 API 为准。不要把网页、Python 或 Android 示例直接当成 Unity C# API。实现桥接时应完成：

1. 选择一张稳定、正面且置信度足够的脸；多人同时出现时，先只锁定最大或最近的一张，防止脸形在两人间跳跃。
2. 将 landmark 的 X/Y 从归一化图像坐标转换为以脸中心为原点的本地坐标；Y 轴通常需要翻转；Z 要按同一尺度归一化。用眼距或脸框宽度作尺度基准。
3. 根据 MediaPipe Face Mesh 的**官方三角形连接表**随机选择三角形，用重心权重 `p = a*A + b*B + c*C`（`a+b+c=1`）生成 `particleCount` 个位置；这样才有连续的脸表面，而不是只有 478 个散点。
4. 用同样的重心权重插值得到 UV，在相机 `Color32[] pixels` 中采样。镜像预览时须同步镜像 U；Y 要按 Unity 纹理和相机帧实际方向验证。只在新帧到达时读取颜色，避免每粒子调用 `GetPixel`。
5. 为 FaceTarget 加时间平滑（例如 3–6 帧滑动平均或指数平滑）。只在 High 前/High 进入时传给 `SetFaceTargets`，不要每帧重置目标。
6. 未检测到脸时保留最近一次合法目标一小段时间，之后平滑退回 Idle；不要输出零数组，否则粒子会突然塌到原点。

推荐的桥接数据结构是 `Vector3[] sampledPositions`、`Vector2[] sampledUvs`、`Color[] sampledColors`，长度固定为粒子数。MediaPipe 的地标和三角形表只在更新目标时计算；`ParticleFaceController` 只负责动画和提交到粒子系统。

## 第 3 阶段：接入既有声音系统

`AuthorityVoiceClassifier` 目前已连接 `AuthorityVisualController`。新增一个公开方法或 UnityEvent 适配层，含义应明确：

```csharp
public void OnAuthorityClassified(bool isHigh)
{
    particleFace.SetState(isHigh
        ? ParticleFaceController.FaceState.High
        : ParticleFaceController.FaceState.Low);
}
```

High 触发时若当前摄像头尚无稳定的人脸，保持 Low 或显示「等待面部」提示，不应把旧观众脸误归给新输入。Low 的回弹时长可与既有 `lowDissolveDuration` 视觉节奏协调，但无需复用其文字粒子参数。

## 多人融合：后续方案，不是本次原型

多人融合不应等于把每帧所有脸平均。更可控的作品规则是：每次被赋予 High 权重的观众产生一个冻结的 `FaceContribution`（位置、颜色、时间、身份/追踪 ID、权重），随后在粒子层进行渐进混合。

- **几何**：对同一粒子索引的目标位置做加权平均，或按区域（眼、鼻、嘴、轮廓）让后来贡献覆盖该区域；必须以稳定的统一拓扑/采样种子为前提。
- **颜色**：在线性颜色空间做加权混合，避免直接在 gamma 空间平均造成发灰；可为不同观众设置衰减或保留权重。
- **身份与隐私**：仅保存处理后的低分辨率点云或短期内存数据，提供超时清除和明确的现场告知；未经同意不要保存可还原的人脸影像。
- **追踪**：多人场景应使用稳定 track ID；追踪丢失、遮挡和转头后要有过渡策略。

这属于第 3 阶段实验。先完成单人、正面、稳定光照下的 High/Low 原型，再评估是否真的需要多人实时融合。

## 验收与排错

1. 不装摄像头/MediaPipe：Play 后 Idle 是稳定云团；Inspector 切 High 后为数学脸；Low 有短暂扰动并回聚；没有 Console 错误或粒子泄漏。
2. 摄像头：检查权限、设备名、首帧尺寸与镜像方向；仅在新帧处理，记录平均帧耗时。
3. 人脸：检查脸中心、眼距、Y 翻转和 Z 比例；从侧面看应有有限深度，但不要宣称恢复了完整后脑。
4. 颜色：显示调试 UV/RawImage，确认左右眼没有反色或镜像；光线变化不应导致每帧闪烁。
5. 声音：用已有 High/Low 测试路径确认每次分类只触发一次状态切换；Low 不得永久写入人脸目标。
6. 性能：先 1,000 粒子，再 3,000；若 CPU 的 `SetParticles` 成为瓶颈，才将数据移至 VFX Graph。先验证视觉正确性，不要过早上 GPU 管线。

## 推荐实施顺序

1. 在 Test 3 加入第 1 阶段原型，确定构图、粒子材质和三态叙事。
2. 接入单一摄像头预览与权限处理。
3. 接入 MediaPipe，仅显示 478 个地标并校正坐标/镜像。
4. 用 Face Mesh 三角形采样扩展到 1,000–3,000 粒子，再接 RGB 采样。
5. 以适配器接 High/Low；执行端到端测试。
6. 仅在单人版本稳定后，设计和测试多人贡献规则、清除策略与性能方案。

## 参考

- [MediaPipe Face Landmarker 官方文档](https://ai.google.dev/edge/mediapipe/solutions/vision/face_landmarker)
- [Unity WebCamTexture API](https://docs.unity3d.com/ScriptReference/WebCamTexture.html)
- [Unity ParticleSystem.SetParticles API](https://docs.unity3d.com/ScriptReference/ParticleSystem.SetParticles.html)
- [Unity VFX Graph 文档](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@latest)
