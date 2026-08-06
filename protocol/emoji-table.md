# SAS-Emoji-Tabelle

Die 64 Symbole, aus denen der Short Authentication String gebildet wird.
Fünf Emojis = 30 Bit. Siehe [`docs/05-security.md` §5.4](../docs/05-security.md).

**Die Reihenfolge ist Teil des Protokolls** und darf sich nie ändern — sie ist in drei
Implementierungen dupliziert (`EmojiTable.cs`, `EmojiTable.swift`, `generate.mjs`), und alle
drei müssen identisch sein.

| Index | Symbol | Index | Symbol | Index | Symbol | Index | Symbol |
|---|---|---|---|---|---|---|---|
| 0 | 🐙 | 16 | 🏰 | 32 | 🗿 | 48 | 🎪 |
| 1 | 🍒 | 17 | 🎺 | 33 | 🥁 | 49 | 🎷 |
| 2 | 🚀 | 18 | 🦋 | 34 | 🦉 | 50 | 🦩 |
| 3 | 🔔 | 19 | 🍅 | 35 | 🍑 | 51 | 🍓 |
| 4 | 🎩 | 20 | 🚂 | 36 | 🚁 | 52 | 🚜 |
| 5 | 🌵 | 21 | 🌟 | 37 | ☀ | 53 | ❄ |
| 6 | 🐘 | 22 | 🐢 | 38 | 🐝 | 54 | 🐿 |
| 7 | 🍕 | 23 | 🍇 | 39 | 🥕 | 55 | 🌰 |
| 8 | ⚓ | 24 | ⛺ | 40 | 🏔 | 56 | 🗼 |
| 9 | 🎸 | 25 | 🎻 | 41 | 🎹 | 57 | 🪕 |
| 10 | 🦊 | 26 | 🦁 | 42 | 🦜 | 58 | 🦔 |
| 11 | 🍄 | 27 | 🍉 | 43 | 🍩 | 59 | 🍍 |
| 12 | 🚲 | 28 | 🛵 | 44 | ⛵ | 60 | 🛸 |
| 13 | 🌙 | 29 | 🌈 | 45 | 🌻 | 61 | 🍀 |
| 14 | 🐧 | 30 | 🐳 | 46 | 🐬 | 62 | 🦭 |
| 15 | 🍋 | 31 | 🍞 | 47 | 🧀 | 63 | 🥨 |
