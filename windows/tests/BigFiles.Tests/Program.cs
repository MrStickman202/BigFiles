using BigFiles.Core;

// Tiny self-contained test runner (no test framework needed): dotnet run --project tests/BigFiles.Tests
int failed = 0, passed = 0;

void Check(bool ok, string what)
{
    if (ok) passed++;
    else { failed++; Console.WriteLine("FAIL: " + what); }
}

void Eq<T>(T actual, T expected, string what) =>
    Check(EqualityComparer<T>.Default.Equals(actual, expected), $"{what}: expected «{expected}», got «{actual}»");

var known = new KnownPaths
{
    WindowsDir = @"C:\Windows",
    ProgramFiles = @"C:\Program Files",
    ProgramFilesX86 = @"C:\Program Files (x86)",
    ProgramData = @"C:\ProgramData",
    Profile = @"C:\Users\me",
    LocalAppData = @"C:\Users\me\AppData\Local",
    RoamingAppData = @"C:\Users\me\AppData\Roaming",
    UserFolders = new[] { @"C:\Users\me\Desktop", @"C:\Users\me\Documents", @"C:\Users\me\Downloads", @"C:\Users\me\OneDrive" },
    FixedDrives = new[] { @"C:\", @"D:\" },
};

InstalledApp App(string name, string? loc = null, string? uninstall = "x.exe /uninstall", string? pfn = null) =>
    new() { Id = "HKLM\\" + name, Name = name, Location = loc, UninstallString = pfn == null ? uninstall : null, PackageFamilyName = pfn };

var apps = new List<InstalledApp>
{
    App("Google Chrome", @"C:\Program Files\Google\Chrome\Application"),
    App("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application"),
    App("Steam", @"C:\Program Files (x86)\Steam"),
    App("Counter-Strike 2", @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive"),
    App("League of Legends", @"C:\Riot Games\League of Legends"),
    App("Discord", @"C:\Users\me\AppData\Local\Discord"),
    App("Microsoft Visual Studio Code", @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\"),
    App("Mozilla Firefox (x64 en-US)", @"C:\Program Files\Mozilla Firefox"),
    App("Epic Games Launcher", @"C:\Program Files (x86)\Epic Games\"),
    App("7-Zip 23.01 (x64)", null),
    App("Spotify Music", pfn: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0"),
    App("Bad Entry", @"C:\Program Files"),
};
apps.Add(App("Hades", @"E:\SteamLibrary\steamapps\common\Hades", uninstall: "steam://uninstall/1145360"));
var index = new AppIndex(apps, known.NotAppLocations(), known.WindowsDir);

// ---------- Name keys ----------
Eq(NameKey.Clean("Mozilla Firefox (x64 en-US)"), "mozillafirefox", "clean firefox");
Eq(NameKey.Clean("7-Zip 23.01 (x64)"), "7zip", "clean 7zip");
Eq(NameKey.Clean("PyCharm 2024.1"), "pycharm", "clean pycharm");
Eq(index.ByName("Chrome", true)?.Name, "Google Chrome", "suffix match Chrome");
Eq(index.ByName("Chrome", false)?.Name, null, "no suffix match when not allowed");
Eq(index.ByName("Cache", true)?.Name, null, "generic names don't fuzzy-match");
Eq(index.ByName("7-Zip", true)?.Name, "7-Zip 23.01 (x64)", "7-Zip folder");
Eq(index.ByLocation(@"C:\Program Files")?.Name, null, "bad InstallLocation ignored");
Eq(AppIndex.PrettyPackageName("Microsoft.WindowsTerminal_8wekyb3d8bbwe"), "Windows Terminal", "pretty package");
Eq(AppIndex.FamilyFromFullName("Microsoft.WindowsTerminal_1.18.10301.0_x64__8wekyb3d8bbwe"), "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "pfn from full name");

// ---------- Path helpers ----------
Eq(PathUtil.Normalize("\"C:/Program Files/App/\""), @"C:\Program Files\App", "normalize");
Eq(PathUtil.Normalize("c:"), @"C:\", "normalize drive");
Check(PathUtil.IsSameOrUnder(@"C:\a\b", @"C:\a"), "under");
Check(!PathUtil.IsSameOrUnder(@"C:\ab", @"C:\a"), "not under (prefix)");
Check(PathUtil.IsSameOrUnder(@"C:\a", @"C:\"), "under drive root");
Eq(PathUtil.Parent(@"C:\a"), @"C:\", "parent of top folder");
Eq(string.Join("|", PathUtil.RemoveNested(new[] { @"C:\a\b", @"C:\a", @"D:\x" })), @"C:\a|D:\x", "remove nested");

// ---------- Grouping ----------
var cls = new Classifier(known, index);
(string, GroupKind) G(string path)
{
    var f = cls.FrameFor(path);
    return (f.Group.Name, f.Group.Kind);
}
void Grp(string path, string name, GroupKind kind) => Eq(G(path), (name, kind), "group of " + path);

Grp(@"C:\Program Files\Google\Chrome\Application\120.0\chrome.dll.dir", "Google Chrome", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Google\Chrome\User Data", "Google Chrome", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Google\Software Reporter Tool", "Google", GroupKind.AppData);
Grp(@"C:\Program Files (x86)\Microsoft\Edge\Application", "Microsoft Edge", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Microsoft\Edge\User Data", "Microsoft Edge", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Microsoft\Windows\INetCache", "Microsoft", GroupKind.AppData);
Grp(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game", "Counter-Strike 2", GroupKind.App);
Grp(@"C:\Program Files (x86)\Steam\steamapps\common\Some Unregistered Game", "Some Unregistered Game", GroupKind.App);
Grp(@"C:\Program Files (x86)\Steam\steamapps\shadercache\730", "Steam", GroupKind.App);
Grp(@"D:\SteamLibrary\steamapps\common\Elden Ring\Game", "Elden Ring", GroupKind.App);
Grp(@"D:\SteamLibrary\steamapps\downloading", @"D:\SteamLibrary", GroupKind.Folder);
Grp(@"E:\SteamLibrary\steamapps\common\Hades\x64", "Hades", GroupKind.App);
Grp(@"E:\SteamLibrary\steamapps\shadercache\1145360", @"E:\SteamLibrary", GroupKind.Folder);
Grp(@"E:\SteamLibrary\steamapps\common\Other Game", "Other Game", GroupKind.App);
Grp(@"C:\Riot Games\League of Legends\Game", "League of Legends", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Riot Games\League of Legends", "League of Legends", GroupKind.App);
Grp(@"C:\Riot Games\Riot Client", @"C:\Riot Games", GroupKind.Folder);
Grp(@"C:\Users\me\AppData\Local\Discord\app-1.0", "Discord", GroupKind.App);
Grp(@"C:\Users\me\AppData\Roaming\discord\Cache", "Discord", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\bin", "Microsoft Visual Studio Code", GroupKind.App);
Grp(@"C:\Users\me\AppData\Roaming\Code\User", "Microsoft Visual Studio Code", GroupKind.App);
Grp(@"C:\Program Files\Mozilla Firefox", "Mozilla Firefox (x64 en-US)", GroupKind.App);
Grp(@"C:\Users\me\AppData\Roaming\Mozilla\Firefox\Profiles", "Mozilla Firefox (x64 en-US)", GroupKind.App);
Grp(@"C:\Program Files\7-Zip", "7-Zip 23.01 (x64)", GroupKind.App);
Grp(@"C:\Program Files (x86)\Epic Games\Fortnite\FortniteGame", "Fortnite", GroupKind.App);
Grp(@"C:\Program Files (x86)\Epic Games\Launcher\Engine", "Epic Games Launcher", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Packages\SpotifyAB.SpotifyMusic_zpdnekdrzrea0\LocalCache", "Spotify Music", GroupKind.App);
Grp(@"C:\Users\me\AppData\Local\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe", "Windows Terminal", GroupKind.AppData);
Grp(@"C:\Program Files\WindowsApps\SpotifyAB.SpotifyMusic_1.2.3.0_x86__zpdnekdrzrea0", "Spotify Music", GroupKind.App);
Grp(@"C:\Users\me\AppData\Roaming\SomeOldApp\logs", "SomeOldApp", GroupKind.AppData);
Grp(@"C:\Program Files\Unregistered Tool\bin", "Unregistered Tool", GroupKind.AppData);
Grp(@"C:\Users\me\Documents\Projects", @"~\Documents", GroupKind.Folder);
Grp(@"C:\Users\me\OneDrive\Pictures", @"~\OneDrive", GroupKind.Folder);
Grp(@"C:\Users\me\.vscode\extensions", @"~\.vscode", GroupKind.Folder);
Grp(@"C:\Users\me\AppData\Local\Temp\x", "Temporary files", GroupKind.System);
Grp(@"C:\Users\Public\Documents", @"C:\Users\Public", GroupKind.Folder);
Grp(@"C:\Windows\System32", "Windows", GroupKind.System);
Grp(@"C:\Windows\WinSxS\Steam", "Windows", GroupKind.System);
Grp(@"C:\$Recycle.Bin\S-1-5-21", "Recycle Bin", GroupKind.System);
Grp(@"C:\Windows.old\Windows", "Windows.old", GroupKind.System);
Grp(@"C:\System Volume Information", "Windows system files", GroupKind.System);
Grp(@"D:\Photos\2023", @"D:\Photos", GroupKind.Folder);
Grp(@"C:\ProgramData\Microsoft\Windows Defender", "Microsoft", GroupKind.AppData);
Grp(@"C:\Program Files\Common Files\microsoft shared", "Common Files", GroupKind.System);
Grp(@"C:\", "Other", GroupKind.System);
Eq(cls.FileRoot(cls.DriveFrame(@"C:\"), @"C:\pagefile.sys", "pagefile.sys").Group.Name, "Windows system files", "pagefile group");
Eq(cls.FileRoot(cls.DriveFrame(@"C:\"), @"C:\random.bin", "random.bin").Group.Name, "Other", "loose root file group");

// One app = one group with several roots.
var lol = cls.Table.Groups.Single(g => g.Name == "League of Legends");
Check(lol.Roots.Any(r => r.Path == @"C:\Riot Games\League of Legends" && r.IsInstall), "LoL install root");
Check(lol.Roots.Any(r => r.Path == @"C:\Users\me\AppData\Local\Riot Games\League of Legends" && !r.IsInstall), "LoL data root");

// ---------- Scanner with an in-memory file system ----------
var fs = new FakeFs();
long MBs(double n) => (long)(n * Format.MB);
fs.File(@"C:\Riot Games\League of Legends\Game\data.wad", MBs(800));
fs.File(@"C:\Riot Games\League of Legends\Game\small.dll", MBs(0.5));
fs.File(@"C:\Users\me\AppData\Local\Riot Games\League of Legends\logs.txt", MBs(30));
fs.File(@"C:\Users\me\Documents\big.iso", MBs(2000));
fs.File(@"C:\Users\me\Documents\vm.vhdx", MBs(120));
for (int i = 0; i < 400; i++) fs.File($@"C:\Users\me\Documents\photos\img{i:000}.jpg", MBs(2 + i * 0.01));
for (int i = 0; i < 50; i++) fs.File($@"C:\Users\me\Documents\notes\n{i}.txt", 4096);
fs.File(@"C:\Users\me\BigFilesTest\test.bin", MBs(60));
fs.File(@"C:\Users\me\NTUSER.DAT", MBs(5));
fs.File(@"C:\Program Files\App\a.bin", MBs(10), fileId: 77);
fs.File(@"C:\Program Files\App\hardlink-of-a.bin", MBs(10), fileId: 77);
fs.Dir(@"C:\Users\me\AppData\Local\Application Data", skip: true); // junction loop
fs.File(@"C:\Users\me\AppData\Local\Application Data\loop.bin", MBs(999));
fs.Unreadable(@"C:\Users\me\AppData\Local\Secret");

var cls2 = new Classifier(known, index);
var scanner = new Scanner(cls2, fs);
var data = scanner.Run(new[] { @"C:\Program Files", @"C:\Users\me", @"C:\Riot Games\League of Legends", @"C:\Users\me\Documents" });

Eq(data.ScanRoots.Count, 3, "nested scan roots removed");
Eq(data.Errors, 1, "unreadable folder counted");
long expected = MBs(800) + MBs(0.5) + MBs(30) + MBs(2000) + MBs(120) + Enumerable.Range(0, 400).Sum(i => MBs(2 + i * 0.01))
                + 50 * 4096 + MBs(60) + MBs(5) + MBs(10);
Eq(data.Bytes, expected, "total bytes (hard link once, junction skipped)");
var lol2 = data.Table.Groups.Single(g => g.Name == "League of Legends");
Eq(lol2.Size, MBs(830.5), "LoL group total = install + AppData");
Eq(lol2.VisibleRoots.Count(), 2, "LoL has 2 roots");
Eq(lol2.Items.Count, 2, "LoL items ≥ 1 MB");
var docs = data.Table.Groups.Single(g => g.Name == @"~\Documents");
Eq(docs.Items.Count(i => i.Kind == ItemKind.Bundle), 1, "vhdx is a bundle");
Eq(docs.Files, 2 + 400 + 50, "documents file count");

// ---------- Tree rows: sizes add up ----------
var o = new ViewOptions { MinSize = 50 * Format.MB };
var groups = TreeBuilder.Groups(data, o);
Check(groups.All(r => r.Size >= o.MinSize), "groups ≥ filter");
Eq(groups[0].Name, @"~\Documents", "sorted by size desc");
var docRow = groups.Single(r => r.Name == @"~\Documents");
var kids = TreeBuilder.Children(docRow, data, o);
Eq(kids.Sum(k => k.Size), docRow.Size, "group children add up");
var smaller = kids.Single(k => k.Type == RowType.Smaller);
var smallKids = TreeBuilder.Children(smaller, data, o);
Eq(smallKids.Sum(k => k.Size), smaller.Size, "smaller files children add up");
Eq(smallKids.Count(k => k.Type == RowType.SmallerItem), 300, "max 300 smaller items");
var more = smallKids.Single(k => k.Type == RowType.Remainder);
Check(more.Name == "150 more files", "remainder row: " + more.Name);
Check(smallKids.Where(k => k.Type == RowType.SmallerItem).All(k => k.Size < o.MinSize), "smaller items under filter");

var lolRow = groups.Single(r => r.Name == "League of Legends");
var lolKids = TreeBuilder.Children(lolRow, data, o);
Eq(lolKids.Sum(k => k.Size), lolRow.Size, "LoL children add up");
Eq(lolRow.Location, "2 locations", "LoL location column");
var lolSmall = lolKids.Single(k => k.Type == RowType.Smaller);
var lolSmallKids = TreeBuilder.Children(lolSmall, data, o);
Eq(lolSmallKids.Sum(k => k.Size), lolSmall.Size, "LoL smaller adds up");
Eq(lolSmallKids.Last().Name, "Tiny files (under 1 MB each)", "tiny files row");

var o1 = new ViewOptions { MinSize = Format.MB };
var docKids1 = TreeBuilder.Children(TreeBuilder.Groups(data, o1).Single(r => r.Name == @"~\Documents"), data, o1);
Eq(docKids1.Sum(k => k.Size), docs.Size, "1 MB filter adds up");
Eq(docKids1.Last().Name, "Tiny files (under 1 MB each)", "1 MB filter: tiny row");

var sortedByName = TreeBuilder.Groups(data, new ViewOptions { MinSize = Format.MB, Sort = SortColumn.Name, Descending = false });
Check(sortedByName.Select(r => r.Name).SequenceEqual(sortedByName.Select(r => r.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)), "sort by name");

var search = TreeBuilder.Groups(data, new ViewOptions { MinSize = Format.MB, Search = "big.iso" });
Eq(search.Count, 1, "search finds item's group");
Eq(TreeBuilder.Children(search[0], data, new ViewOptions { MinSize = Format.MB, Search = "big.iso" }).Count, 1, "search shows matching items only");
var searchGroup = TreeBuilder.Groups(data, new ViewOptions { MinSize = Format.MB, Search = "league" });
Check(TreeBuilder.Children(searchGroup.Single(), data, new ViewOptions { MinSize = Format.MB, Search = "league" }).Any(k => k.Type is RowType.Smaller or RowType.Remainder), "matching group shows all its items");

// ---------- Deleting ----------
var prot = new Protection(known);
Check(prot.Reason(@"C:\Users\me\Documents") != null, "Documents protected");
Check(prot.Reason(@"C:\Windows\Temp\x.log") != null, "inside Windows protected");
Check(prot.Reason(@"D:\") != null, "drive root protected");
Check(prot.Reason(@"C:\Users\me\AppData\Roaming") != null, "Roaming protected");
Check(prot.Reason(@"C:\Users\me\Documents\big.iso") == null, "file in Documents allowed");
Check(prot.Reason(@"C:\Users\me\BigFilesTest") == null, "test folder allowed");
Check(prot.Reason(@"C:\pagefile.sys") != null, "pagefile protected");

var refused = Deletion.Plan(data, new[] { docRow }, prot);
Eq(refused.Targets.Count, 0, "deleting ~\\Documents is refused");
Eq(refused.Refused.Count, 1, "refusal reported");

var opts1 = new ViewOptions { MinSize = Format.MB };
var testRow = TreeBuilder.Groups(data, opts1).Single(r => r.Name == @"~\BigFilesTest");
var testItem = TreeBuilder.Children(testRow, data, opts1).First(r => r.Item != null);
var plan = Deletion.Plan(data, new[] { testRow, testItem }, prot);
Eq(plan.Targets.Count, 1, "group + its item → only the group");
Eq(plan.Targets[0].Path, @"C:\Users\me\BigFilesTest", "target is the group root");
Eq(plan.TotalSize, MBs(60), "space freed");
long before = data.Bytes, filesBefore = data.Files;
foreach (var t in plan.Targets) Deletion.ApplyRemoved(data, t);
Eq(data.Bytes, before - MBs(60), "total bytes drop after delete");
Eq(data.Files, filesBefore - 1, "file count drops after delete");
Check(!TreeBuilder.Groups(data, opts1).Any(r => r.Name == @"~\BigFilesTest"), "deleted group disappears");

var lolPlan = Deletion.Plan(data, new[] { TreeBuilder.Groups(data, o).Single(r => r.Name == "League of Legends") }, prot);
Eq(lolPlan.Targets.Count, 1, "app with uninstaller: only data folder");
Eq(lolPlan.KeptInstalls.Single(), "League of Legends", "install folder kept");

var isoRow = TreeBuilder.Children(TreeBuilder.Groups(data, o).Single(r => r.Name == @"~\Documents"), data, o).Single(r => r.Name == "big.iso");
var isoPlan = Deletion.Plan(data, new[] { isoRow }, prot);
long docsBefore = docs.Size;
Deletion.ApplyRemoved(data, isoPlan.Targets.Single());
Eq(docs.Size, docsBefore - MBs(2000), "file delete updates group");
Check(docs.Items.All(i => i.Name != "big.iso"), "item removed");

// "This also removes data of": a folder that contains another group's folder.
var cls3 = new Classifier(known, index);
var fs3 = new FakeFs();
fs3.File(@"C:\Users\me\AppData\Local\Google\loose.bin", MBs(70));
fs3.File(@"C:\Users\me\AppData\Local\Google\Chrome\User Data\cache.bin", MBs(90));
var data3 = new Scanner(cls3, fs3).Run(new[] { @"C:\Users\me\AppData\Local" });
var googleRow = TreeBuilder.Groups(data3, o).Single(r => r.Name == "Google");
var gPlan = Deletion.Plan(data3, new[] { googleRow }, prot);
Eq(gPlan.AlsoRemoves.SingleOrDefault(), "Google Chrome", "also removes data of Chrome");
Eq(gPlan.TotalSize, MBs(160), "freed includes nested group");

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

sealed class FakeFs : IFileSystem
{
    readonly Dictionary<string, List<RawEntry>> dirs = new(PathUtil.Cmp);
    readonly HashSet<string> unreadable = new(PathUtil.Cmp);

    List<RawEntry> Ensure(string dir)
    {
        if (dirs.TryGetValue(dir, out var l)) return l;
        dirs[dir] = l = new List<RawEntry>();
        var parent = PathUtil.Parent(dir);
        if (parent != null)
        {
            var pl = Ensure(parent);
            if (!pl.Any(e => PathUtil.Cmp.Equals(e.Name, PathUtil.Leaf(dir))))
                pl.Add(new RawEntry { Name = PathUtil.Leaf(dir), IsDirectory = true });
        }
        return l;
    }

    public void File(string path, long size, long fileId = 0) =>
        Ensure(PathUtil.Parent(path)!).Add(new RawEntry { Name = PathUtil.Leaf(path), Size = size, FileId = fileId });

    public void Dir(string path, bool skip)
    {
        var pl = Ensure(PathUtil.Parent(path)!);
        pl.Add(new RawEntry { Name = PathUtil.Leaf(path), IsDirectory = true, Skip = skip });
    }

    public void Unreadable(string path)
    {
        Ensure(path);
        unreadable.Add(path);
    }

    public bool ReadDirectory(string dir, List<RawEntry> entries)
    {
        if (unreadable.Contains(dir) || !dirs.TryGetValue(dir, out var l)) return false;
        entries.AddRange(l);
        return true;
    }
}
