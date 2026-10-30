# Test 2：局部累积人脸融合操作教程

## 这次实现到哪里

场景：`Assets/Scenes/Test 2.unity`，Unity 2022.3.61f1c1。

按照分享对话的最终目标，先实现“后来的观众能修改前一位观众留下的五官”的 Unity 几何融合原型。你已确认暂时没有 MICA/FLAME，先完成 Unity 融合与教程，因此当前 A/B 是算法生成的两组**模拟五官**，不会重建摄像头中的真人，也不能据此判断真人相似度。现阶段不采集摄像头，不融合照片、肤色和纹理。

完成内容：眼部、鼻部、嘴部、脸型和整脸的平滑遮罩；逐次累积；后人覆盖前人；1.5 秒形变过渡；High/Low 声音分类事件接入；保存/读取；顶点模板校验和位移限制。原来的 HeadFaceFusion 照片贴面组件仅在 Test 2 禁用。其他场景不启用新功能。

## 第一步：打开场景，确认接线

1. 在 Project 窗口进入 `Assets > Scenes`，双击 **Test 2**。
2. 在 Hierarchy 选中 **Authoritycontroller**，打开 Inspector。
3. 找到新增的 **Cumulative Face Fusion** 组件。Head 已指向场景中的 `head`，Visual Controller 已指向同一个物体上的 `AuthorityVisualController`。
4. 确认 **Show Controls** 已勾选。High Influence 默认 `0.85`，Low Influence 默认 `0.2`，Transition Seconds 默认 `1.5`。
5. 原有 **Head Face Fusion** 的勾选框应关闭。不要同时打开两个效果，以免照片掩盖几何变化。

这些接线已经保存到场景，正常情况下无需手工拖拽。

## 第二步：先做一次鼻部变化

1. 点击 Unity 顶部三角形 **Play**，切换到 **Game** 标签。
2. 左上角会出现 `TEST 2 | Cumulative geometry prototype` 面板。它明确显示 `SIMULATED VIEWERS`。
3. 点击 **Viewer A**，选择模拟观众 A。选中本身不会改变脸。
4. 关闭 **Auto cycle**，让区域按钮出现。
5. 点击 **Nose**（鼻子）。其他按钮为 Eyes（眼部）、Mouth（嘴）、Contour（脸型）、Whole face（整脸）。
6. 把 **Influence** 滑块拖到约 `85%`。
7. 点击一次 **Apply once**，等待 1.5 秒。鼻部沿深度方向改变，面板显示类似 `#1: Nose / 85%`。
8. 如果鼻部深度从正面不明显，可以运行时切到 Scene，从侧面观察，再返回 Game。颈部、后脑不会一起移动。

A 的设计偏向较宽眼距、较宽嘴、较突出的鼻部；B 的方向相反。这是验证机制的人工目标，不代表两位真实观众。

## 第三步：验证不同人的五官可以共存

1. 保持刚才的 A 鼻子结果，点击 **Viewer B**。
2. 点击 **Eyes**，Influence 设为 `85%`，再点击 **Apply once**。
3. 观察眼部改变。鼻子保留上一步 A 留下的形态，眼部开始呈现 B 的模拟特征。
4. 切到 **Viewer A > Mouth > Apply once**，让嘴部留下 A 的特征。
5. 每次点击都基于当前累计结果计算，不会自动退回最初的脸。少量重叠区域会按照平滑遮罩交叠，这是避免硬接缝的设计。

## 第四步：验证后人能覆盖前人的同一个五官

1. 点击 **Viewer B > Nose**。
2. Influence 调到 `20%`，点击一次 **Apply once**。鼻部只向 B 靠近一小步，仍保留较多 A 的痕迹。
3. Influence 改为 `85%`，再点击一次。相同区域明显向 B 的目标靠近。
4. 注意不要连续猛点来比较 20% 和 85%：20% 重复多次也会累积覆盖。公平比较时，用 Reset face 恢复后重做同一套初始步骤。
5. **Whole face** 会同时影响多个面部区域，但同样避开颈部和后脑。

公式：`新面容 = 当前面容 + 权重 × 区域遮罩 × (观众目标 - 当前面容)`。遮罩边缘的实际影响比中心小。

## 第五步：接声音与自动分区

1. 勾选 **Auto cycle**，评价依次影响 `Eyes → Nose → Mouth → Contour → Eyes...`。
2. 先点击一下 Game 内部，使窗口获得键盘焦点。
3. 按 **H**，沿用原有 High 文字效果，并对本次区域应用 `85%` 的融合。
4. 按 **L**，沿用 Low 文字效果，并应用 `20%` 的融合。
5. **R** 沿用原来的文字序列重置，**不会清除累计面容**。要清除面容，请点面板中的 **Reset face**。
6. 在原有“语音调试”窗口等待录音系统就绪，按原有数字 `1` 快捷键开始/结束录音（或用窗口的录音按钮）。现有分类器得到 High/Low 后，会触发同样的融合事件。无需为了本演示进行真人录音；按钮和 H/L 可独立测试。
7. 当前分类器输出两个档位，所以融合使用两个可配置权重；85%/20% 是作品规则，不是科学测得的“权威性百分比”。手动滑块只控制 Apply once，不修改 H/L 的权重。
8. 同一个文字序列的重复 PlaySequence 调用受原控制器的去重逻辑限制；结束后或重置后再开始下一次。面板 Apply once 每次点击都会应用一次。

本阶段即使录入真人声音，面部目标仍然是当前选中的模拟 A/B。

## 第六步：保存、退出与恢复

1. 满意后点击 **Save**。保存的是已经提交的完整累计面容与评价次数；即使还在过渡动画中，保存的也会是本次最终目标。
2. 文件位于 `Application.persistentDataPath/Test2-cumulative-face.json`。macOS 当前项目通常为 `~/Library/Application Support/DefaultCompany/studio3/Test2-cumulative-face.json`，Console 的保存日志会输出实际路径。
3. 点击 **Reset face**，验证脸恢复最初形态。Reset 不会覆盖已有保存文件。
4. 点击 **Load**，恢复刚才保存的形态。
5. 停止 Play 再进入后，先显示原脸；点击 Load 才恢复存档，不会自动载入。
6. Save 会更新同一个存档；若要保留多份，先手工复制并重命名旧存档。
7. 文件仅保存当前几何和少量计数，不保存每位模拟观众历史、录音或照片。换用不同模板的存档会被拒绝。

## 第七步：调整变化强度与排查

- **High/Low Influence**：调整两个声音档位的覆盖程度，范围 0–1。
- **Transition Seconds**：调整形变动画时长，默认 1.5 秒。
- **Automatic Region**：打开后顺序轮换，关闭后固定影响所选 Region。
- **Maximum Displacement**：相对于原始头部包围盒对角线的最大位移比例，默认 0.15。它是安全范围，不是融合强度。过小会拒绝目标或存档。
- **Show Controls**：展览时可隐藏演示面板，但这一步应在停止 Play 后设置再保存场景。
- Inspector 中 Play 期间的修改通常会在退出后还原。要保留配置，请退出 Play 再修改并保存场景。
- 没有面板：检查场景是不是 Test 2、组件是否启用、Show Controls 是否开启、Console 是否有编译错误。
- 变化不明显：使用手动 Whole face / 100% 比较 A 与 B；查看 Scene 侧面，确认不是只在切换 Viewer 而没有 Apply once。
- 面板太大：放大 Game 面板，或调整 Game 分辨率/Scale。不要把 Game 缩得太小。
- H/L 无效：先点击 Game 内部；检查 AuthorityVisualController 的 Enable Keyboard Testing 是否开启。

## 第八步：以后替换为真实观众

[MICA 官方源码](https://github.com/Zielon/MICA)和 [FLAME 官方页面](https://flame.is.tue.mpg.de/)是后续重建阶段的入口。该阶段尚未安装或实现。本原型的遮罩针对当前项目头模标定，不能直接套在另一个 FLAME 头上。

1. 先取得所需 MICA 权重、FLAME 模型及两组照片，在独立 Python 环境完成静态重建，验证能产生不同人的几何。
2. 选择统一模板。推荐使用同版本 FLAME 的中性头和所有观众目标，而不是把 FLAME 的顶点数组直接塞给当前 `head.fbx`。
3. 若保留当前 FBX，先建立 FLAME → 当前 FBX 的可靠配准/形变传递。顶点数相同也不等于顶点顺序或语义相同。
4. 若换成 FLAME 中性头，需要重新接 Head、适配渲染源，重新标定 CumulativeFaceMath 的区域遮罩，最好换用该版本 FLAME 官方顶点区域标签并做平滑。
5. 对外部重建结果统一比例、朝向、位置和中性表情；转换为**被修改的 MeshFilter 的本地坐标**，不是 Unity 世界坐标或摄像机坐标。
6. Unity 对接代码调用 `fusion.SetRegisteredTarget(vertices, fusion.TemplateFingerprint)`；只有返回 true 才应用评价。指纹必须由可靠的转换器在验证模板和索引映射后附带，不能给任意模型简单贴上当前指纹来绕过检查。
7. 通过 `fusion.ApplyEvaluation(normalizedInfluence)` 每句评价调用一次。不要每帧或每个音频采样调用，否则会迅速覆盖前人。若用此直接评分入口，应取消同一次评价的 High/Low 事件触发，避免重复融合。
8. SetRegisteredTarget 只做数组/模板声明/有限数值/位移校验，不代替解剖配准，也不能从数据本身证明对应关系正确。
9. 确认几何融合后，再分别开发局部纹理、摄像头采样、Python 通信和实时重建。

## 已执行的验证

Unity 菜单 **Tools > Leaf > Validate Cumulative Face Fusion** 可重新运行验证：累积权重、同一区域覆盖、零权重保留、区域隔离、颈部/后脑保护、现有人头五类遮罩、每个顶点连续 100 次混合的位移限制，以及模板指纹一致性。通过后 Console 输出 `Cumulative face validation PASSED`，摘要写入忽略的 `Temp/CumulativeFaceChecks.txt`。

已在 Unity 编辑器运行该验证，并进入 Test 2 Play 确认面板和累计形变。真人重建和真实录音到融合的端到端流程不属于本阶段已验证内容。
