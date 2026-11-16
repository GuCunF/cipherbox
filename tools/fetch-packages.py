# -*- coding: utf-8 -*-
"""
下载自包含发布所需的 NuGet 包到 packages-local。

背景：本机 .NET 的 HTTPS 走 Schannel，连 nuget.org 会报 NU1301
（安全包中没有可用的凭证）；而 Python 的 OpenSSL 通路是正常的，
所以用 Python 下载，再让 dotnet 从本地目录做还原。

用法：python tools\\fetch-packages.py
"""

import json
import os
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FEED = os.path.join(ROOT, "packages-local")

# 运行时包版本要与机器上安装的 .NET 运行时版本一致
RUNTIME_VERSION = "10.0.13"

FIXED = [
    ("microsoft.netcore.app.runtime.win-x64", RUNTIME_VERSION),
    ("microsoft.aspnetcore.app.runtime.win-x64", RUNTIME_VERSION),
    ("microsoft.windowsdesktop.app.runtime.win-x64", RUNTIME_VERSION),
]


def list_versions(name: str) -> list[str]:
    url = f"https://api.nuget.org/v3-flatcontainer/{name}/index.json"
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.load(response)["versions"]


def grab(name: str, version: str) -> None:
    os.makedirs(FEED, exist_ok=True)
    out = os.path.join(FEED, f"{name}.{version}.nupkg")
    if os.path.exists(out):
        print(f"已存在 {name} {version}")
        return
    url = f"https://api.nuget.org/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg"
    urllib.request.urlretrieve(url, out)
    print(f"已下载 {name} {version} ({os.path.getsize(out):,} 字节)")


def main() -> None:
    for name, version in FIXED:
        grab(name, version)

    stable = [v for v in list_versions("microsoft.net.illink.tasks") if v.startswith("10.") and "-" not in v]
    grab("microsoft.net.illink.tasks", stable[-1])


if __name__ == "__main__":
    main()
