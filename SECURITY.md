# 安全说明

## 受支持版本

当前仅支持最新发布的 v0.2.x 系列；更早版本可能缺少修复，不承诺安全更新。

## 报告漏洞

请通过 GitHub 的**私密安全公告**（Security Advisory）提交漏洞，不要在 Issue、
PR 或聊天中公开细节：

1. 打开 [RemoteCI 安全公告](https://github.com/MEMZ-Edge01/RemoteCI/security/advisories/new)。
2. 填写漏洞描述、影响范围与复现信息，并注明“私密报告”。
3. 文档仓库的问题同样通过 RemoteCI 的安全公告提交，再在正文中关联
   [RemoteCI-Docs](https://github.com/MEMZ-Edge01/RemoteCI-Docs)。

收到报告后会尽快评估并回复；修复发布前请不要公开漏洞细节。

## 数据处理

本系统保存密码哈希、设备会话验证器等敏感数据。涉及认证、会话或权限的改动
必须额外说明其安全影响，并在合并前经过至少一次人工审核。
