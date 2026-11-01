# SignPath Foundation 免费代码签名申请材料

> SignPath Foundation 为**开源项目**提供免费 Windows 代码签名（消除 SmartScreen "未知发布者"）。
> 官方入口：<https://signpath.org/> ・ 申请：<https://signpath.org/apply>
> 本文是可直接复制填表的材料草稿，基于本仓库当前状态整理。

---

## 一、项目基本信息

| 字段 | 内容 |
| --- | --- |
| 项目名称 | **NetCable** |
| 项目定位 | NETworkManager 的跨平台（Windows/macOS/Linux）重写版，32 个网络工具 |
| 仓库地址 | https://github.com/JohnChan1017/netcable |
| 许可证 | **GPL-3.0**（OSI 认可） |
| 主语言 / 技术栈 | C# / .NET 10 + Avalonia（桌面 UI） |
| 支持的平台 | Windows x64（签名对象）；macOS arm64、Linux x64 同时发布 |
| 维护者 | GitHub @JohnChan1017 |
| 首次发布 | v1.0.0 |
| 当前状态 | 可构建、可运行、CI 产出安装包；32 个工具页全部通过截图冒烟 |

## 二、为什么需要签名

1. Windows 用户下载未签名 MSI/EXE 时，SmartScreen 显示"未知发布者"，安装率与信任度受损。
2. 本项目是**开源免费**工具，无力承担商业代码签名证书（OV 约 ¥1500–3000/年，且 2023 起强制硬件令牌）。
3. 签名对象：GitHub Releases 中的 **MSI** 与 **portable ZIP 内的 NetCable.exe**（仅 Windows 产物）。

## 三、构建与产物（CI 可复现）

- CI：GitHub Actions，工作流文件 [`.github/workflows/build.yml`](../.github/workflows/build.yml)
- 触发：push 到 `main`、打 tag `v*`、或手动 `workflow_dispatch`
- Windows 构建步骤：
  1. `setup-dotnet` 安装 .NET 10
  2. `dotnet publish -c Release -r win-x64 --self-contained true`（自包含，目标机无需 .NET）
  3. `dotnet tool install --global wix --version 5.0.2`
  4. `wix build installer/Product.wxs ... -d InstallScope=perUser|perMachine` 产出 MSI
  5. `Compress-Archive` 产出 portable zip
  6. 打 tag 时通过 `softprops/action-gh-release` 自动创建 Release 并上传产物

**签名建议做法**：在 CI 中用 SignPath GitHub Action，对 `publish/win-x64/**/*.exe` 与 `*.msi` 做签名（MSI 需在 link 阶段签名，或对 MSI 内的 exe 签名后重新打包，二者可选其一，官方文档推荐对 exe 签名并对 MSI 做 dual sign）。

## 四、需要的签名范围

| 产物 | 类型 | 是否需签名 |
| --- | --- | --- |
| `NetCable-<ver>-win-x64.msi` | MSI | ✅ |
| `NetCable-<ver>-win-x64-perMachine.msi` | MSI | ✅ |
| `portable/NetCable.exe` | PE | ✅ |
| portable 目录下的 `.dll` | PE | 可选（第三方 Avalonia/.NET 库不建议改签） |

## 五、待填信息（申请前补全）

- [ ] 真实姓名 / 邮箱（申请表单要求，签名证书主体会体现）
- [ ] 项目主要用途的一句话描述（英文，用于证书）
- [ ] 是否有其他维护者 / 组织（可选）
- [ ] 确认仓库为 public（当前 ✅）
- [ ] 确认 License 文件存在（`LICENSE`，GPL-3.0 ✅）
- [ ] 确认 CI 在最近 30 天内成功运行（推送后触发一次 ✅）

## 六、申请后的接入步骤（拿到证书后）

> ✅ **CI 侧已接好**：`.github/workflows/build.yml` 里已经内置 SignPath 签名步骤，
> 默认全部跳过；只要填上下面 4 个配置项就自动生效，**不需要再改 workflow 文件**。

1. 在 SignPath 后台创建 project（slug 建议 `netcable`）与 signing policy（建议 `release-signing`），
   签名策略里勾选：`NetCable.exe`（PE）、`*.msi`（MSI 需允许"sign MSI"或对内部文件签名）。
2. 在 GitHub 仓库 **Settings → Secrets and variables → Actions** 配置：

| 类型 | 名称 | 值 |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | SignPath 生成的 API Token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath 组织 ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | `netcable` |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | `release-signing` |

3. 填好后打 tag（`git tag v1.0.1 && git push origin v1.0.1`）验证：
   - CI 会先把未签名产物（`out/*.msi` + `out/win-x64/NetCable.exe`）上传为 artifact `unsigned-windows`
   - `Submit signing request to SignPath` 等待签名完成（`wait-for-completion: true`）
   - `Replace with signed binaries and repack` 用签名后的 exe/MSI 覆盖并重新打 portable zip
   - `Verify signatures` 用 `Get-AuthenticodeSignature` 校验，**状态必须是 `Valid`**，否则 CI 失败
   - Release 上传 `out/*.msi` 与 `out/*.zip` → 即签名后的产物

4. 若 `SIGNPATH_PROJECT_SLUG` 留空，签名步骤自动跳过，CI 与现在行为完全一致。

> 注意：`signpath/github-action-submit-signing-request@v1` 的版本号以 SignPath 官方文档为准，
> 如官方发布新主版本，可把 `@v1` 改为对应 tag。

## 七、附：当前版本信息（填写申请表时可用）

- 版本：`1.0.0`
- Windows 产物 SHA256（perUser MSI）：见 `docs/packaging.md` 或 Release 页
- 代码规模：C# + XAML 约 175 文件 / 16,000 行；服务层 48 文件
- 测试：Core 单测 16 项；服务层自测 160+ 断言；32 个工具页截图冒烟
