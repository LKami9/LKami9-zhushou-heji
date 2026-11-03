#!/usr/bin/env python3
"""校验 TreeHouseModules.json 与模块文件是否一致。

Omni 加载在线模块前会比对清单里的 Sha256 与下载到的文件字节，
不一致直接拒载；而且只改哈希不抬 Version 时已安装用户不会收到更新。
这两件事手工维护很容易出错，所以在 CI 里每次 push 都校验一遍。

本地也可以直接跑：python tools/verify_manifest.py
"""

from __future__ import annotations

import hashlib
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "TreeHouseModules.json"

errors: list[str] = []
warnings: list[str] = []


def fail(msg: str) -> None:
    errors.append(msg)


def main() -> int:
    if not MANIFEST.is_file():
        print(f"找不到清单：{MANIFEST}")
        return 1

    try:
        data = json.loads(MANIFEST.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        print(f"清单不是合法 JSON：{exc}")
        return 1

    if data.get("SchemaVersion") != 1:
        fail(f"SchemaVersion 应为 1，实际 {data.get('SchemaVersion')!r}")

    modules = data.get("Modules")
    if not isinstance(modules, list) or not modules:
        print("清单里没有 Modules")
        return 1

    seen_names: dict[str, int] = {}
    seen_files: dict[str, int] = {}

    for index, module in enumerate(modules, start=1):
        name = module.get("InternalName", f"<第 {index} 项>")
        prefix = f"[{index}] {name}"

        # --- 必填字段 ---
        for key in ("InternalName", "Author", "RepositoryURL", "Version", "ApiVersion", "MinimumOmniVersion", "File", "Sha256"):
            if not module.get(key):
                fail(f"{prefix}: 缺少字段 {key}")

        if module.get("ApiVersion") != 1:
            fail(f"{prefix}: ApiVersion 必须是 1（宿主 1.1.8.2+ 强制），实际 {module.get('ApiVersion')!r}")

        title = (module.get("Localization") or {}).get("Source", {}).get("Title")
        desc = (module.get("Localization") or {}).get("Source", {}).get("Description")
        if not title:
            fail(f"{prefix}: Localization.Source.Title 为空")
        if not desc:
            fail(f"{prefix}: Localization.Source.Description 为空")

        # --- 重名 / 重复文件 ---
        if name in seen_names:
            fail(f"{prefix}: InternalName 与第 {seen_names[name]} 项重复")
        seen_names[name] = index

        rel = module.get("File")
        if not rel:
            continue
        if rel in seen_files:
            fail(f"{prefix}: File 与第 {seen_files[rel]} 项重复（{rel}）")
        seen_files[rel] = index

        path = ROOT / rel
        if not path.is_file():
            fail(f"{prefix}: 文件不存在 {rel}")
            continue

        raw = path.read_bytes()

        # --- 哈希：清单里存大写十六进制 ---
        actual = hashlib.sha256(raw).hexdigest().upper()
        declared = str(module.get("Sha256", "")).strip().upper()
        if actual != declared:
            fail(
                f"{prefix}: Sha256 不一致 —— 清单 {declared or '(空)'}，实际 {actual}\n"
                f"    修复：把清单里的 Sha256 改成 {actual}，并递增 Version"
            )

        # --- 文件内容自检（避免清单写 ApiVersion 1 但代码还是旧 API）---
        text = raw.decode("utf-8", errors="replace")
        if "[ModuleApiVersion(1)]" not in text:
            fail(f"{prefix}: 文件里没有 [ModuleApiVersion(1)]")
        if not re.search(r":\s*OmniModuleBase\b", text):
            fail(f"{prefix}: 文件里没有继承 OmniModuleBase（可能还是旧版 ModuleBase）")
        if not re.search(r"class\s+" + re.escape(name) + r"\b", text):
            fail(f"{prefix}: 文件里找不到名为 {name} 的类（类名必须等于 InternalName）")

        # --- 换行符：仓库里必须是 LF，否则玩家下载后哈希对不上 ---
        if b"\r\n" in raw:
            fail(f"{prefix}: 文件含 CRLF，仓库里必须是 LF")

        # --- 版本号格式提醒 ---
        version = str(module.get("Version", ""))
        if not re.fullmatch(r"\d+(\.\d+){1,3}", version):
            warnings.append(f"{prefix}: Version {version!r} 不是常见的 x.y.z 形式")

        print(f"  ✓ {name}  {version}  {rel}")

    print()
    for warning in warnings:
        print(f"提醒：{warning}")

    if errors:
        print()
        for error in errors:
            print(f"错误：{error}")
        print(f"\n共 {len(errors)} 项错误，清单与文件不一致，宿主会拒载或已安装用户不会更新。")
        return 1

    print(f"清单校验通过：{len(modules)} 个模块，哈希全部一致。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
