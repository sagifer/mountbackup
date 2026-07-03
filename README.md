# Mount Backup — N.I.N.A. plugin

NINA 3.2 plugin **kuplung nélküli** (pl. harmonic drive) mountokhoz. Az ilyen mechanika
kikapcsolva garantáltan nem mozdul el fizikailag, de lefagyás / áramszünet után elveszti a
koordináta-tudását. A plugin:

- **másodpercenként fájlba menti** a mount pozícióját **Alt/Az-ban** (időfüggetlen formátum),
  SSD-kímélő módon (sorok hozzáfűzése + rotáció 256 KB felett, visszaolvasáskor az utolsó
  érvényes sor nyer);
- a mount **csatlakozásakor vagy unparkolásakor visszasync-eli** az utolsó mentett pozíciót —
  a mentett Alt/Az-ból az *aktuális* időpontra számolt RA/Dec-kel, így akármennyi idő telt el,
  a mount koordinátában is ugyanoda mutat, ahova fizikailag;
- ha a tracking nem megy, a sync idejére bekapcsolja, és **a sync után mindenképpen kikapcsolja
  a követést**;
- minden lépést időbélyeggel logol a saját dockable paneljére (Imaging fül) és a NINA logba,
  a fontos eseményekről (sikeres/sikertelen restore, riasztás) **NINA toast értesítést** is ad;
  a logolás az Options oldalon kikapcsolható (hibák és riasztások ilyenkor is logolódnak);
- **eltérés-küszöb**: ha a mount hitt pozíciója a beállított szögön (alapból 1°) belül egyezik a
  mentettel, a sync kimarad — egészséges éjszakán a plugin hozzá sem nyúl a mounthoz (0 = mindig
  sync-el);
- **fagyás-őrkutya**: ha tracking közben a jelentett Alt/Az a beállított ideig (alapból 60 s)
  egyáltalán nem változik, az RA és a driver szideridő-órája alapján dönt: ha a szideridő sem
  halad → a driver/kapcsolat fagyott le; ha az RA szideridő-ütemben kúszik → a mount áll
  (nem követ); ha az RA stabil és a szideridő halad → a mount rendben követ, csak a driver
  durva felbontásban adja az Alt/Az-t — ilyenkor **nincs** riasztás (ez okozta a korábbi
  téves riasztásokat);
- **Restore now** gomb: kézi visszaállítás bármikor (az auto-restore kapcsolót és a küszöböt
  figyelmen kívül hagyja);
- a **Reset** gomb törli a mentett pozíciót — utána nem történik visszaállítás, amíg új mentés
  nem készül.

Pozíciófájl: `%LOCALAPPDATA%\NINA\MountBackup\position_<profilId>.csv` (profilonként külön).

## Fordítás és telepítés (Windows gépen)

Követelmény: .NET 8 SDK (vagy Visual Studio 2022).

```bat
cd <ez a mappa>
dotnet build -c Release
```

A post-build lépés automatikusan bemásolja a DLL-t ide:
`%LOCALAPPDATA%\NINA\Plugins\3.0.0\MountBackup\` — a NINA induláskor átmigrálja a saját
verziómappájába. **Fordítás előtt zárd be a NINA-t** (a betöltött DLL zárolva van), majd
indítsd újra.

Ellenőrzés:
1. NINA → Options → Plugins → Installed: megjelenik a **Mount Backup**.
2. Imaging fül → jobb felső panelválasztó: kapcsold be a **Mount Backup** panelt.
3. Ha a plugin nem töltődik be: `%LOCALAPPDATA%\NINA\Logs\` legfrissebb logfájljában keresd a
   `MountBackup` / MEF composition hibákat.

Tipp: a mentett pozíció frissessége a NINA *Device Polling Interval* beállításától függ
(alapból 2 s) — Options → General alatt vedd 1 s-ra.

## Tesztforgatókönyvek (ASCOM Telescope Simulatorral)

- csatlakozás unparkolt mounttal → azonnali restore-sync;
- csatlakozás parkolt mounttal → restore az unpark pillanatában (addig a mentés szünetel);
- külső unpark (másik ASCOM kliensből) → a pollozott AtPark-átmenet is triggerel;
- tracking kikapcsolva restore-kor → a plugin bekapcsolja, sync-el, majd kikapcsolja;
- sync után a tracking **mindig** kikapcsol, akkor is, ha előtte be volt kapcsolva;
- Reset függő restore közben → a restore elmarad, a mentés folytatódik;
- a profil `No Sync` telescope-beállítása mellett a sync elutasítását a log és egy hiba-toast jelzi;
- **Restore now** gomb → kézi sync auto-restore kikapcsolva is (toast + log);
- **küszöb**: egészséges connect (a mount jó pozíciót jelent) → „sync skipped" log/toast;
  küszöb 0-ra állítva a sync mindig lefut;
- **őrkutya**: timeout 10 s-ra állítva, tracking be, majd a driver-kommunikáció megszakítása
  (pl. USB kihúzás) → egyszeri hiba-toast + log; a pozíció újbóli változásával újraéled;
- **őrkutya, téves riasztás ellenpróba**: tracking egészségesen fut, de a driver percre
  kerekített Alt/Az-t ad (vagy pólusközeli cél) → nincs riasztás, egyszeri info-log jelzi,
  hogy a durva Alt/Az felbontást észlelte.

## Felépítés

| Fájl | Felelősség |
|---|---|
| `MountBackup/MountBackupPlugin.cs` | plugin manifest, Options oldal DataContextje, Teardown |
| `MountBackup/Services/MountBackupService.cs` | 1 s mentő ciklus, mount események, restore állapotgép |
| `MountBackup/Services/PositionFileStore.cs` | CSV append/rotáció/betöltés/törlés |
| `MountBackup/Services/SavedPosition.cs` | egy minta + CSV (de)szerializálás |
| `MountBackup/Dockables/MountBackupDockableVM.cs` | Imaging fül panel viewmodel |
| `MountBackup/Dockables/MountBackupDockableView.xaml` | panel DataTemplate (kulcs: `MountBackup.Dockables.MountBackupDockableVM_Dockable`) |
| `MountBackup/Options/PluginOptionsView.xaml` | Options oldal (kulcs: `Mount Backup_Options`) |
