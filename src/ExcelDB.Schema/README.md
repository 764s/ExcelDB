# ExcelDB.Schema 实施状态

本目录是按 `docs/modules/01-schema.md` 建立的正式 M1 工程，不延续已删除的 PoC。

当前实现包括：

- v2 `exceldb/options.proto`，以及只使用随包 `windows-x64` protoc 的离线编译边界；
- canonical descriptor v1、完整 shape/option 物化、M1 lint、确定性 bytes 与 xxHash64；
- `AssetIdentity = (tableId, rowGuid)` 对应的 `RowRef { table, row_guid }` wire 契约；
- 全量 authoring 与 per-target runtime C#、M7 `RuntimeSchemaRegistry`/binding、artifact manifest/hash；
- 纯内存 create/edit/retire proto mutation、数字身份建议，以及计划期 `ITableInitializer`/`IExportTargetStrategy` 接线。

本项目只生成候选 proto 与派生产物内容，不直接提交文件。原子目录替换、自包含 CLI 发布及跨模块流水线编排由后续 Tooling/CLI 模块负责。
