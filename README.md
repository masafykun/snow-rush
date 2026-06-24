# 🏂 SNOW RUSH

> ゲートを切り抜けてゲレンデを滑り降りる、ワンタッチ スノーボード スラローム。

スノーボードでゲレンデを高速で滑り降り、次々と現れるゲートをカービングで切り抜けていくワンタッチ アーケードゲームです。Unity 製の WebGL ビルドで、ブラウザから直接プレイできます。

![Unity](https://img.shields.io/badge/Unity-6000.0.77f1-000000?style=flat-square&logo=unity)
![WebGL](https://img.shields.io/badge/WebGL-990000?style=flat-square&logo=webgl)
![C#](https://img.shields.io/badge/C%23-239120?style=flat-square&logo=csharp)

🔗 **[Live Demo](https://masafykun.github.io/snow-rush/)**

---

## 📸 スクリーンショット
![screenshot](screenshot.png)

---

## 🎮 操作方法
| 操作 | 動作 |
|---|---|
| タップ / クリック（左右） | ボードを左右にカービング |
| ゲートを通過 | フルスピードでゲートを切り抜ける |

---

## ✨ 特徴
- **ダウンヒル スラローム** — フルスピードでゲートをカービングして通過
- **ワンタッチ操作** — シンプルな操作で楽しめるアーケード設計
- **スピード感** — ゲレンデを一気に滑り降りる爽快な滑走

---

## 🛠️ 技術スタック
| カテゴリ | 技術 |
|---|---|
| ゲームエンジン | Unity 6000.0.77f1 |
| 言語 | C#（`src/` 配下） |
| ビルド | WebGL |
| 配信 | GitHub Pages |

---

## 🚀 セットアップ

```bash
# WebGL ビルドはブラウザで直接プレイ可能
# Live Demo: https://masafykun.github.io/snow-rush/

# ローカルで動かす場合（CORS 回避のため簡易サーバー経由で開く）
python3 -m http.server 8000
# ブラウザで http://localhost:8000/ を開く
```

C# ソースは `src/` ディレクトリにあります。Unity（6000.0.77f1）でプロジェクトとして開けます。

---

## ライセンス

このリポジトリには現時点で LICENSE ファイルが含まれていません。再利用を検討される場合は、リポジトリ作者までお問い合わせください。

© 2026 masafykun (https://github.com/masafykun)
