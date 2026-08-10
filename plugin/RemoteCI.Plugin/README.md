# RemoteCI 插件

为 ClassIsland 2.x 开发的课表手表联动插件。

## 功能

- 将当前课/下一节课/倒计时/周次实时推送到 Wear OS 手表
- 上课、下课、放学事件推送到手表（通知+振动）
- 手表可切换单双周（v0.1 本地覆盖；v0.2 接入 ProfileService 真实换课）

## 使用

1. 在 ClassIsland 中安装本插件（.cipx 或开发目录加载）
2. 打开 设置 → RemoteCI 设置，填写配对码与云端地址
3. 手表端连接：局域网直连 `ws://电脑IP:8765/ws/配对码`，或经云端服务端中转

## 开发

- 构建：`dotnet build plugin/RemoteCI.Plugin`
- 打包 cipx：`dotnet build plugin/RemoteCI.Plugin -p:CreateCipx=true`
- 调试：参考 ClassIsland 插件文档，配置 `ClassIsland_DebugBinaryFile` 环境变量
