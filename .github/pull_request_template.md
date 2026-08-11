## 变更说明

<!-- 说明本次变更解决的问题、实现方式与用户可见影响。 -->

## 关联变更

- 功能文档 PR（RemoteCI-Docs）：<!-- 填写链接，无则填“无” -->

## 检查清单

- [ ] 只修改了本任务相关问题，没有夹带无关改动
- [ ] 代码包含必要的注释，命名清晰，采用项目现有主流方案
- [ ] 已运行 `dotnet build RemoteCI.slnx -c Release` 与 `dotnet test RemoteCI.slnx -c Release`
- [ ] 手表端改动已运行 `./gradlew :app:assembleDebug :app:testDebugUnitTest`
- [ ] 用户可见功能变化已同步到 RemoteCI-Docs 文档仓库
- [ ] 示例与文档中不包含真实密码、令牌、配对码或内部地址
- [ ] 已阅读并遵守 [行为准则](../CODE_OF_CONDUCT.md) 与 [贡献指南](../CONTRIBUTING.md)
