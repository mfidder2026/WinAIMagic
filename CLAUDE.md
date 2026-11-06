# WinAIMagic

The Windows version of [MacAIMagic](https://github.com/mfidder2026/MacAIMagic). The two share no code: this one is C# on .NET 10 with WinUI 3, the Mac one is Swift. Each is built and tested on its own platform, by a Claude on that machine. What is said in a session on one machine does not reach the other; only what is on GitHub does.

## Keeping the two versions alike

The owner types a change once, on the machine in front of them. On the other machine they only say something like "Neem de nieuwe wijzigingen uit MacAIMagic over". So the commit has to be enough to work from.

### After a change here

Write a commit message that stands on its own:

- what changed for the user, and why;
- what was measured or run, and what was not;
- a last line saying what the Mac should do with it, one of:
  - `Mac: take over`
  - `Mac: adapt` and what differs
  - `Mac: not needed` and why, for a change that is Windows-only

Commit on `main` and push: the other machine only sees `main`.

### Taking over from the Mac

The last Mac commit taken over is named at the top of [docs/PARITY.md](docs/PARITY.md).

1. List what is new. Replace `<commit>` with that marker:

   ```bash
   gh api repos/mfidder2026/MacAIMagic/compare/<commit>...main --jq '.commits[] | .sha[0:7] + " " + (.commit.message | split("\n")[0])'
   ```

2. Read each commit's diff (`gh api repos/mfidder2026/MacAIMagic/commits/<sha> -H "Accept: application/vnd.github.diff"`) and decide per change: take over, adapt, or does not apply. A commit that says `Takes over WinAIMagic <sha>` came from here and needs nothing. Skip the Mac's zipped releases in `RELEASE/`.
3. Take care with memory and engine changes. Copying them is usually wrong:
   - The Mac has one pool of memory. The planner here was redesigned for VRAM + RAM + SSD, so a Mac reserve or limit may mean something else here, or nothing.
   - Work out what the change does on a card of 16 GB, 8 GB and none, by computing plans, and take it over only where it helps. `MemoryPlanner` is a pure function, so tests can describe any PC.
   - A model is never called "too big" here (test `ThereIsNoThirdLabel`).
4. Build and test here. Use the Mac's test names where they apply: `scripts/parity.py` matches on them.
5. Never present the Mac's numbers as measured here. Write what was measured on this platform, and say so when something is an estimate.
6. Update the marker in `docs/PARITY.md`, note what was adapted or left out, and regenerate the test list: `python scripts/parity.py <path to a MacAIMagic checkout>`.
7. Put `Takes over MacAIMagic <sha>` in the commit message. Report to the owner in plain words what was taken over, what was adapted and what does not apply.

## Building and testing

```bash
dotnet test tests/WinAIMagic.Engine.Tests
```

The other test projects are `WinAIMagic.Chat.Tests`, `WinAIMagic.Studio.Tests` and `WinAIMagic.Documents.Tests`. Run all four before a commit.

- **Texts:** Dutch in the code is the key, through `L("...")`. Every new text needs a translation in `src/WinAIMagic.Core/i18n/{en,de,fr}.json`, with the keys sorted. Check with `python scripts/i18n_check.py`, and write "pc" rather than "Mac".
- **Version:** `<Version>` in `Directory.Build.props`. The app shows it in the sidebar and on the System page.
- **Image engine:** `scripts/fetch-image-engine.sh` fetches it into `engine/image-engine` at one commit, `scripts/build-image.bat cuda|vulkan|cpu` builds it into `build/image-<backend>`, and `scripts/package.py` puts it beside the second engine as `wam-image.exe`. Say "the image engine" in the app and the docs; its real name is only in the scripts and the licence notices.
- **Release:** `scripts/package.py` builds three files in `dist`: the setup (everything), `WinAIMagic-Portable-<v>.zip` (no NVIDIA engines, data beside the exe) and `WinAIMagic-NVIDIA-<v>.zip`. Attach all three to the release `v<v>`: the portable app downloads the NVIDIA zip from exactly that address and checks its sha256. Publish only when the owner asks.
- **UI checks:** start the app with `--data=<dir>`, so the owner's settings and models stay out of it.
- **Model files:** every address in `src/WinAIMagic.Core/Resources/models.json` names one commit, never `main`, because a sha256 belongs to one version of a file. Every Monday `.github/workflows/check-models.yml` runs `scripts/check_catalog.py` on GitHub's machines, and it reads headers only.
  - A failed run means a model can no longer be downloaded. Point its addresses at a commit that still has the files, or take over the new files with their sizes and hashes, then ask the owner about a new version.
  - When taking over new files, compare their headers with the old ones. If the tensors differ, `memory` and `dense` in the catalog change too, and the app's numbers for that model need checking.
  - A maker's newer version is only listed in the run's summary. Taking it over is the owner's choice: the app then compares the sha256 in each file's .ok with the catalog's and offers Update on the card, which downloads only the files that differ.

## Rules from the owner

- The owner changes system settings and the BIOS; Claude does not.
- API keys stay encrypted in `settings.json`.
- The name of the upstream engine stays out of the app, the docs and this file. Say "the main engine" and "the second engine".
