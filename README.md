# 850 Messenger

**Classic ICQ, rebuilt.** A native Windows messenger by [WARCORE](https://area850.com).

Sign in with a **UIN** (your number). Add people by number or nickname — they have to authorize you. Contacts stay on the list when they’re offline.

Website: [area850.com](https://area850.com)  
Host: `icq.area850.com`

---

## What it is

Two desktop programs:

| App | What it does |
|---|---|
| **850 Messenger** | Contacts, chat, calls, rooms, admin (UIN 0) |
| **850 Server** | Runs the net. Tray + live stats. Keep this running. |

Native **WPF / .NET 8** client. Native **WinForms + ASP.NET Core** server. SQLite on disk. Not a website in a box.

---

## Sign in

- **UIN** is your number (like ICQ). New accounts get one when you register.
- Admin is **UIN 0**.
- Optional **Invisible** on sign-in — others won’t see you.
- **Dark mode** toggle is on the sign-in screen (and later in the contact list / My details).

---

## Messenger

**Statuses:** Online, Away, Occupied, Do not disturb, Invisible. Auto-away after 5 minutes idle.

**Contacts**
- Add by UIN or nickname (authorization required)
- Stay on the list when offline
- Right-click: message, nudge, voice/video, details, send file, copy UIN, block, delete
- Offline mail waits until they sign in
- Typing shows in 1:1 chat

**Chat**
- Original colored 850 smileys (`:)`, `:(`, `:D`, `; )`, `:P`, `<3`, `:850:`, …)
- Nudge, file send (online, under 15 MB)
- Close the window to keep running in the tray

**Calls:** 1:1 voice and video (WebRTC).

**Rooms** live in **channels** — Hangout, Music, Tech, Gaming, Dating, After Dark, Area 850, War Room.

- Anyone can **create a room** and become **owner**
- Promote mods / room admins, bounce, mute, ban
- Password lock, lock the door (mods only), slow mode, welcome / topic
- **Hold Ctrl to talk** (or hold the green bar). Cam live and mic hot show next to names.

**Admin (UIN 0)** — native panel in the messenger: overview, users, rooms, traffic. Same tools live in **850 Server**.

**My details:** nickname, status message, about, city, gender, always on top, dark mode.

---

## Server

- Listen: `http://127.0.0.1:8500` (LAN printed in the window)
- Data file: `%LocalAppData%\Area850\850.db` (SQLite)
- Stores: accounts (password hashed), profiles, friends, rooms, chat history, offline mail
- Does **not** store: who’s online right now (RAM only — they sign in again after a restart)
- Voice/video after connect is peer-to-peer; chat goes through the server

---

## Build

```text
dotnet build Area850.csproj -c Release
dotnet build Server/Area850.Server.csproj -c Release
```

Run **850 Server** first, then **850 Messenger**.

---

## Credits

**WARCORE**  
[area850.com](https://area850.com)

---

## Private docs

GitHub Pages (private): see `docs/index.html` on the `gh-pages` / docs site once enabled.
