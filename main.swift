// BigFiles — lists every file on your Mac by size.
// Apps and other bundles (.app, .photoslibrary, .framework, …) count as ONE item
// with their total size; the files inside them are never listed separately.

import SwiftUI
import AppKit
import Darwin

// MARK: - Helpers

func fmt(_ bytes: Int64) -> String {
    ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
}

// MARK: - Data

struct Item: Identifiable, Hashable, Sendable {
    enum Kind: String, Sendable { case file = "File", app = "App", package = "Bundle" }

    let path: String
    let name: String
    let size: Int64
    let kind: Kind
    let kindLabel: String
    let displayPath: String
    let owner: String
    var id: String { path }

    init(path: String, size: Int64, kind: Kind, owner: String) {
        self.path = path
        self.size = size
        self.kind = kind
        self.owner = owner
        self.name = (path as NSString).lastPathComponent
        self.kindLabel = kind.rawValue
        self.displayPath = Item.pretty((path as NSString).deletingLastPathComponent)
    }

    static let dataPrefix = "/System/Volumes/Data"
    static let home = NSHomeDirectory()

    /// Shows /System/Volumes/Data/Users/me/x as ~/x
    static func pretty(_ p: String) -> String {
        var s = p
        if s.hasPrefix(dataPrefix + "/") { s = String(s.dropFirst(dataPrefix.count)) }
        if s == home || s.hasPrefix(home + "/") { s = "~" + String(s.dropFirst(home.count)) }
        return s
    }
}

/// One line in the list: either an app/folder group (with children) or a single file.
struct Row: Identifiable, Hashable {
    let id: String
    let name: String
    let size: Int64
    let kindLabel: String
    let location: String
    let iconPath: String?
    let children: [Row]?
}

struct OwnerInfo: Sendable {
    var appPath: String?
}

struct ScanResult: Sendable {
    var items: [Item] = []
    var ownerTotals: [String: Int64] = [:]
    var ownerInfo: [String: OwnerInfo] = [:]
    var files = 0
    var bytes: Int64 = 0
    var errors = 0
}

final class CancelFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false
    func set() { lock.lock(); value = true; lock.unlock() }
    var isSet: Bool { lock.lock(); defer { lock.unlock() }; return value }
}

// MARK: - Which app does a folder belong to?

/// Installed apps, used to turn folder names like "com.isaacmarovitz.Whisky" or "Steam"
/// into the app they belong to.
struct AppIndex: Sendable {
    struct Entry: Sendable { let name: String; let path: String }
    private var byKey: [String: Entry] = [:]
    private var bundleIDs: [(id: String, entry: Entry)] = []

    static func norm(_ s: String) -> String {
        String(s.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) })
    }

    init() {}

    static func build() -> AppIndex {
        var idx = AppIndex()
        let dirs = ["/Applications", "/Applications/Utilities", "/System/Applications",
                    "/System/Applications/Utilities", Item.home + "/Applications"]
        for d in dirs {
            guard let names = try? FileManager.default.contentsOfDirectory(atPath: d) else { continue }
            for n in names where n.hasSuffix(".app") {
                let path = d + "/" + n
                let name = (n as NSString).deletingPathExtension
                let entry = Entry(name: name, path: path)
                let k = norm(name)
                if idx.byKey[k] == nil { idx.byKey[k] = entry }
                if let bid = Bundle(path: path)?.bundleIdentifier {
                    idx.byKey[norm(bid)] = idx.byKey[norm(bid)] ?? entry
                    idx.bundleIDs.append((bid.lowercased(), entry))
                }
            }
        }
        return idx
    }

    /// Returns the installed app for a folder/bundle-id name, if there is one.
    func app(for raw: String) -> Entry? {
        var s = raw
        if s.hasSuffix(".plist") { s = String(s.dropLast(6)) }
        if let e = byKey[AppIndex.norm(s)] { return e }
        // Strip "group." / team-ID prefixes: "group.com.foo.bar", "ABCDE12345.com.foo.bar"
        var parts = s.split(separator: ".").map(String.init)
        if parts.count > 2 {
            if parts[0].lowercased() == "group" || (parts[0].count == 10 && parts[0] == parts[0].uppercased()) {
                parts.removeFirst()
            }
            let stripped = parts.joined(separator: ".")
            if let e = byKey[AppIndex.norm(stripped)] { return e }
            let low = stripped.lowercased()
            if let hit = bundleIDs.first(where: { low == $0.id || low.hasPrefix($0.id + ".") }) { return hit.entry }
            if let last = parts.last, let e = byKey[AppIndex.norm(last)] { return e }
        }
        let k = AppIndex.norm(s)
        if k.count >= 4, let hit = byKey.first(where: { $0.key.hasSuffix(k) }) { return hit.value }
        return nil
    }

    /// Name to show when there is no installed app: "com.isaacmarovitz.Whisky" → "Whisky".
    static func fallbackName(_ raw: String) -> String {
        let parts = raw.split(separator: ".").map(String.init)
        if parts.count >= 3, ["com", "org", "io", "net", "app", "group", "de", "co"].contains(parts[0].lowercased()) {
            return parts.last ?? raw
        }
        return raw
    }
}

// MARK: - Scanner (single pass with fts, the same low-level walker `du` and `find` use)

enum Scanner {
    /// Directory extensions treated as a single item. Anything else macOS
    /// reports as a package is caught by the isPackage check below.
    static let packageExts: Set<String> = [
        "app", "framework", "bundle", "plugin", "appex", "xpc", "kext", "pkg", "mpkg",
        "photoslibrary", "musiclibrary", "tvlibrary", "imovielibrary", "fcpbundle",
        "logicx", "band", "xcarchive", "xcodeproj", "xcworkspace", "playground", "docc",
        "sparsebundle", "sparseimage", "vmwarevm", "pvm", "utm", "lrlibrary", "lrdata",
        "rtfd", "pages", "numbers", "key", "saver", "prefpane", "qlgenerator",
        "mdimporter", "component", "vst", "vst3", "aaxplugin", "scptd", "wdgt"
    ]

    /// Library subfolders whose children are named after the app that owns them.
    static let libraryAppFolders: Set<String> = [
        "Application Support", "Caches", "Logs", "Containers", "Group Containers",
        "Saved Application State", "HTTPStorages", "WebKit", "Application Scripts",
        "Cookies", "Preferences", "LaunchAgents"
    ]

    struct InodeKey: Hashable { let dev: Int32; let ino: UInt64 }

    /// True if the last path component has an extension (and isn't a dotfile like .git).
    @inline(__always)
    static func hasExtension(_ p: UnsafeMutablePointer<CChar>) -> Bool {
        guard let dotM = strrchr(p, 0x2E) else { return false }   // '.'
        let dot = UnsafePointer(dotM)
        if dot[1] == 0 { return false }
        if let slashM = strrchr(p, 0x2F) {                         // '/'
            return dot > UnsafePointer(slashM) + 1
        }
        return dot > UnsafePointer(p)
    }

    static func packageKind(_ path: String) -> Item.Kind? {
        let ext = (path as NSString).pathExtension.lowercased()
        if ext == "app" { return .app }
        if packageExts.contains(ext) { return .package }
        if let v = try? URL(fileURLWithPath: path).resourceValues(forKeys: [.isPackageKey]),
           v.isPackage == true {
            return .package
        }
        return nil
    }

    /// Owner bookkeeping while walking: names are interned so the per-file work is an array lookup.
    final class Owners {
        var names: [String] = []
        var weak: [Bool] = []           // true = we only guessed a folder name (e.g. "Google")
        var appPaths: [String?] = []
        var index: [String: Int] = [:]
        let apps: AppIndex
        init(apps: AppIndex) { self.apps = apps }

        func intern(_ name: String, appPath: String? = nil, weak w: Bool = false) -> Int {
            if let i = index[name] {
                if let p = appPath, appPaths[i] == nil || (p.hasPrefix("/Applications/") && !(appPaths[i]!.hasPrefix("/Applications/"))) { appPaths[i] = p }
                if !w { weak[i] = false }
                return i
            }
            names.append(name); appPaths.append(appPath); weak.append(w)
            index[name] = names.count - 1
            return names.count - 1
        }

        /// Owner for a folder/bundle-id name: the installed app if we know it, else the tidied name.
        func named(_ raw: String, weak w: Bool = true) -> Int {
            if let e = apps.app(for: raw) { return intern(e.name, appPath: e.path) }
            return intern(AppIndex.fallbackName(raw), weak: w)
        }
    }

    /// Decides the owner of a directory from its location; nil = inherit the parent's.
    static func ownerRule(path rawPath: String, parent: Int, owners: Owners) -> Int? {
        var n = rawPath
        if n.hasPrefix(Item.dataPrefix + "/") { n = String(n.dropFirst(Item.dataPrefix.count)) }
        let home = Item.home

        func libraryRule(_ c: [Substring]) -> Int? {
            // c = path components below a Library folder
            if c.count == 1 {
                switch c[0] {
                case "Developer": return owners.named("Xcode", weak: false)
                case "Mail": return owners.named("Mail", weak: false)
                case "Messages": return owners.named("Messages", weak: false)
                case "Mobile Documents": return owners.intern("iCloud Drive")
                case "Safari": return owners.named("Safari", weak: false)
                default: return nil
                }
            }
            if c.count == 2, libraryAppFolders.contains(String(c[0])) {
                return owners.named(String(c[1]))
            }
            // "Application Support/Google/Chrome": look one level deeper when the first guess was vague
            if c.count == 3, libraryAppFolders.contains(String(c[0])), owners.weak[parent],
               let e = owners.apps.app(for: String(c[2])) {
                return owners.intern(e.name, appPath: e.path)
            }
            return nil
        }

        if n == home { return owners.intern("Home Folder") }
        if n.hasPrefix(home + "/") {
            let c = n.dropFirst(home.count + 1).split(separator: "/")
            if c[0] == "Library" { return libraryRule(Array(c.dropFirst())) }
            if c.count == 1 {
                let name = String(c[0])
                if name == ".Trash" { return owners.intern("Trash") }
                if name.hasPrefix(".") {
                    let bare = String(name.dropFirst())
                    if let e = owners.apps.app(for: bare) { return owners.intern(e.name, appPath: e.path) }
                    return owners.intern("~/" + name)
                }
                return owners.intern("~/" + name)
            }
            return nil
        }
        if n.hasPrefix("/Library/") {
            return libraryRule(Array(n.dropFirst(9).split(separator: "/")))
        }
        if n.hasPrefix("/Users/Shared/") {
            let c = n.dropFirst(14).split(separator: "/")
            if c.count == 1 { return owners.named(String(c[0])) }
            return nil
        }
        switch n {
        case "/System": return owners.intern("macOS")
        case "/private": return owners.intern("System Data")
        case "/usr": return owners.intern("System Data")
        case "/opt/homebrew", "/usr/local": return owners.intern("Homebrew & tools")
        case "/Library": return owners.intern("System Library")
        default: return nil
        }
    }

    static func scan(roots: [String],
                     apps: AppIndex,
                     minSize: Int64,
                     cancel: CancelFlag,
                     progress: @escaping @Sendable (Int, Int64, String) -> Void) -> ScanResult {
        var result = ScanResult()
        var seenHardLinks = Set<InodeKey>()
        let owners = Owners(apps: apps)
        let other = owners.intern("Other")
        var totals: [Int64] = [0]
        var stack: [Int] = [other]        // owner index per directory level

        // Package currently being summed up (-1 = not inside a package)
        var pkgLevel = -1
        var pkgSize: Int64 = 0
        var pkgPath = ""
        var pkgKind = Item.Kind.package

        var ticks = 0
        var lastReport = DispatchTime.now().uptimeNanoseconds

        var cRoots: [UnsafeMutablePointer<CChar>?] = roots.compactMap { strdup($0) }
        guard !cRoots.isEmpty else { return result }
        defer { for p in cRoots { free(p) } }
        cRoots.append(nil)

        // PHYSICAL: don't follow symlinks. XDEV: stay on this disk (skip external drives / network mounts).
        guard let fts = fts_open(&cRoots, FTS_PHYSICAL | FTS_NOCHDIR | FTS_XDEV, nil) else {
            result.errors = 1
            return result
        }
        defer { fts_close(fts) }

        func bump(_ i: Int, _ size: Int64) {
            while totals.count <= i { totals.append(0) }
            totals[i] += size
        }

        while let ent = fts_read(fts) {
            ticks += 1
            if ticks & 1023 == 0 {
                if cancel.isSet { break }
                let now = DispatchTime.now().uptimeNanoseconds
                if now &- lastReport > 150_000_000 {
                    lastReport = now
                    progress(result.files, result.bytes, String(cString: ent.pointee.fts_path))
                }
            }

            let info = Int32(ent.pointee.fts_info)
            let level = Int(ent.pointee.fts_level)

            switch info {
            case FTS_D:
                // Cloud drives (Google Drive, OneDrive, …) are network-backed: slow to walk and not local disk use.
                if level > 0, ent.pointee.fts_namelen == 12,
                   String(cString: ent.pointee.fts_path).hasSuffix("/Library/CloudStorage") {
                    fts_set(fts, ent, Int32(FTS_SKIP))
                    continue
                }
                while stack.count <= level { stack.append(other) }
                let parentOwner = level > 0 ? stack[level - 1] : other
                stack[level] = parentOwner

                if pkgLevel < 0 {
                    let path = String(cString: ent.pointee.fts_path)
                    // Entering a directory: is it an .app / bundle?
                    if level > 0, hasExtension(ent.pointee.fts_path), let kind = packageKind(path) {
                        pkgLevel = level
                        pkgSize = 0
                        pkgPath = path
                        pkgKind = kind
                        if kind == .app {
                            let name = ((path as NSString).lastPathComponent as NSString).deletingPathExtension
                            let o = owners.apps.app(for: name)
                            stack[level] = owners.intern(o?.name ?? name, appPath: path)
                        }
                    } else if let o = ownerRule(path: path, parent: parentOwner, owners: owners) {
                        stack[level] = o
                    }
                }

            case FTS_DP:
                // Leaving a directory: if it was the package, emit it as one item.
                if level == pkgLevel {
                    if pkgSize >= minSize {
                        result.items.append(Item(path: pkgPath, size: pkgSize, kind: pkgKind,
                                                 owner: owners.names[stack[level]]))
                    }
                    pkgLevel = -1
                }

            case FTS_F, FTS_SL, FTS_SLNONE, FTS_DEFAULT:
                guard let st = ent.pointee.fts_statp else { continue }
                // Real space used on disk (handles sparse files and iCloud placeholders correctly)
                var size = Int64(st.pointee.st_blocks) * 512
                // Count hard-linked files only once
                if st.pointee.st_nlink > 1 {
                    let key = InodeKey(dev: st.pointee.st_dev, ino: st.pointee.st_ino)
                    if !seenHardLinks.insert(key).inserted { size = 0 }
                }
                result.files += 1
                result.bytes += size
                let owner = level > 0 && level - 1 < stack.count ? stack[level - 1] : other
                bump(owner, size)
                if pkgLevel >= 0 {
                    pkgSize += size
                } else if size >= minSize {
                    result.items.append(Item(path: String(cString: ent.pointee.fts_path),
                                             size: size, kind: .file, owner: owners.names[owner]))
                }

            case FTS_DNR, FTS_ERR, FTS_NS:
                result.errors += 1

            default:
                break
            }
        }

        for (i, name) in owners.names.enumerated() where i < totals.count && totals[i] > 0 {
            result.ownerTotals[name] = totals[i]
            result.ownerInfo[name] = OwnerInfo(appPath: owners.appPaths[i])
        }
        return result
    }
}

// MARK: - Model

@MainActor
final class Model: ObservableObject {
    enum Scope: Hashable { case home, wholeMac, folder(URL) }

    @Published var scope: Scope = .home
    @Published private(set) var shown: [Row] = []
    @Published var sortOrder: [KeyPathComparator<Row>] = [KeyPathComparator(\Row.size, order: .reverse)] {
        didSet { refresh() }
    }
    @Published var minSizeMB = 50 { didSet { refresh() } }
    @Published var search = "" { didSet { refresh() } }

    @Published var selection = Set<String>()
    @Published var scanning = false
    @Published var hasScanned = false
    @Published var files = 0
    @Published var bytes: Int64 = 0
    @Published var errors = 0
    @Published var currentPath = ""
    @Published var elapsed: Double = 0

    private var all: [Item] = []
    private var ownerTotals: [String: Int64] = [:]
    private var ownerInfo: [String: OwnerInfo] = [:]
    private var apps = AppIndex()
    private var cancel = CancelFlag()

    /// Everything that's kept in memory is ≥ 1 MB; the picker filters further.
    private let storeThreshold: Int64 = 1_048_576

    var rootPaths: [String] {
        switch scope {
        case .home:
            // Apps + your files + the shared Library, i.e. everything you can actually remove
            return ["/Applications", NSHomeDirectory(), "/Library"]
        case .wholeMac:
            return [FileManager.default.fileExists(atPath: Item.dataPrefix + "/Users") ? Item.dataPrefix : "/"]
        case .folder(let url):
            return [url.path]
        }
    }

    var scopeTitle: String {
        switch scope {
        case .home: return "Apps & Home"
        case .wholeMac: return "Whole Mac"
        case .folder(let url): return url.lastPathComponent
        }
    }

    var shownBytes: Int64 { shown.reduce(0) { $0 + $1.size } }

    private func row(forOwner name: String, children: [Row]) -> Row {
        let appPath = ownerInfo[name]?.appPath
        let kind: String
        let loc: String
        if let p = appPath {
            kind = "App"
            loc = Item.pretty((p as NSString).deletingLastPathComponent)
        } else {
            kind = name.hasPrefix("~/") || name == "Home Folder" ? "Folder" : (name == "Other" || name.hasPrefix("macOS") || name.hasPrefix("System") || name.hasPrefix("iCloud") || name.hasPrefix("Trash") || name.hasPrefix("Homebrew") ? "Other" : "App data")
            loc = ""
        }
        return Row(id: "app:" + name, name: name, size: ownerTotals[name] ?? 0, kindLabel: kind,
                   location: loc, iconPath: appPath, children: children)
    }

    func refresh() {
        let min = Int64(minSizeMB) * 1_048_576
        let q = search.trimmingCharacters(in: .whitespaces)
        var byOwner: [String: [Item]] = [:]
        for it in all where it.size >= min { byOwner[it.owner, default: []].append(it) }

        var rows: [Row] = []
        for (name, total) in ownerTotals where total >= min {
            let ownerMatches = q.isEmpty || name.localizedCaseInsensitiveContains(q)
            var items = byOwner[name] ?? []
            if !ownerMatches {
                items = items.filter {
                    $0.name.localizedCaseInsensitiveContains(q) || $0.displayPath.localizedCaseInsensitiveContains(q)
                }
                if items.isEmpty { continue }
            }
            var kids = items.map {
                Row(id: $0.path, name: $0.name, size: $0.size, kindLabel: $0.kindLabel,
                    location: $0.displayPath, iconPath: $0.path, children: nil)
            }
            if q.isEmpty {
                let listed = items.reduce(Int64(0)) { $0 + $1.size }
                let rest = total - listed
                if rest > 0, rest >= min || !kids.isEmpty {
                    kids.append(Row(id: "rest:" + name, name: "Smaller files", size: rest, kindLabel: "Various",
                                    location: "under \(fmt(min == 0 ? 1 : min)) each", iconPath: nil, children: nil))
                }
            }
            rows.append(row(forOwner: name, children: kids.sorted(using: sortOrder)))
        }
        shown = rows.sorted(using: sortOrder)
    }

    /// Real paths behind the selected rows (a group row stands for its app, if it has one).
    func paths(for ids: Set<String>) -> [String] {
        ids.compactMap { id in
            if id.hasPrefix("app:") { return ownerInfo[String(id.dropFirst(4))]?.appPath }
            if id.hasPrefix("rest:") { return nil }
            return id
        }
    }

    func scan(_ newScope: Scope) {
        scope = newScope
        startScan()
    }

    func startScan() {
        cancel.set()
        let flag = CancelFlag()
        cancel = flag

        let roots = rootPaths
        let apps = AppIndex.build()
        let threshold = storeThreshold
        all = []
        ownerTotals = [:]
        ownerInfo = [:]
        shown = []
        files = 0
        bytes = 0
        errors = 0
        currentPath = roots.first ?? ""
        scanning = true
        hasScanned = true
        let start = Date()

        Task.detached(priority: .userInitiated) {
            let result = Scanner.scan(roots: roots, apps: apps, minSize: threshold, cancel: flag) { f, b, p in
                Task { @MainActor in
                    guard self.cancel === flag else { return }
                    self.files = f
                    self.bytes = b
                    self.currentPath = p
                }
            }
            await MainActor.run {
                guard self.cancel === flag else { return }
                self.all = result.items
                self.ownerTotals = result.ownerTotals
                self.ownerInfo = result.ownerInfo
                self.files = result.files
                self.bytes = result.bytes
                self.errors = result.errors
                self.elapsed = Date().timeIntervalSince(start)
                self.scanning = false
                self.refresh()
            }
        }
    }

    /// Stops the scan and shows whatever was found so far.
    func stop() { cancel.set() }

    func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = "Scan"
        if panel.runModal() == .OK, let url = panel.url {
            scan(.folder(url))
        }
    }

    // MARK: Actions

    func reveal(_ ids: Set<String>) {
        NSWorkspace.shared.activateFileViewerSelecting(paths(for: ids).map { URL(fileURLWithPath: $0) })
    }

    func open(_ ids: Set<String>) {
        for p in paths(for: ids) { NSWorkspace.shared.open(URL(fileURLWithPath: p)) }
    }

    func copyPaths(_ ids: Set<String>) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(paths(for: ids).sorted().joined(separator: "\n"), forType: .string)
    }

    func trash(_ ids: Set<String>) {
        let items = all.filter { ids.contains($0.id) }
        guard !items.isEmpty else { return }
        let total = items.reduce(Int64(0)) { $0 + $1.size }

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = items.count == 1
            ? "Move “\(items[0].name)” to the Trash?"
            : "Move \(items.count) items to the Trash?"
        alert.informativeText = "This frees about \(fmt(total)) once you empty the Trash."
        alert.addButton(withTitle: "Move to Trash")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        var removed = Set<String>()
        var failed: [String] = []
        for item in items {
            do {
                try FileManager.default.trashItem(at: URL(fileURLWithPath: item.path), resultingItemURL: nil)
                removed.insert(item.id)
            } catch {
                failed.append("\(item.name): \(error.localizedDescription)")
            }
        }
        all.removeAll { removed.contains($0.id) }
        for item in items where removed.contains(item.id) {
            ownerTotals[item.owner, default: 0] -= item.size
        }
        refresh()

        if !failed.isEmpty {
            let a = NSAlert()
            a.messageText = "Some items couldn’t be moved to the Trash"
            a.informativeText = failed.prefix(10).joined(separator: "\n")
            a.runModal()
        }
    }

    func openFullDiskAccess() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
    }
}

// MARK: - Icons

@MainActor
enum IconCache {
    static var cache: [String: NSImage] = [:]
    static func icon(_ path: String) -> NSImage {
        if let img = cache[path] { return img }
        let img = NSWorkspace.shared.icon(forFile: path)
        img.size = NSSize(width: 16, height: 16)
        cache[path] = img
        return img
    }
}

// MARK: - Views

struct ContentView: View {
    @EnvironmentObject var model: Model


    private let sizeOptions = [1, 10, 50, 100, 500, 1024, 5120]

    var body: some View {
        VStack(spacing: 0) {
            if model.hasScanned {
                table
            } else {
                emptyState
            }
            Divider()
            statusBar
        }
        .searchable(text: $model.search, placement: .toolbar, prompt: "Filter by name or path")
        .navigationTitle("BigFiles")
        .toolbar {
            ToolbarItem(placement: .navigation) {
                Menu {
                    Button("Apps & Home Folder") { model.scan(.home) }
                    Button("Whole Mac") { model.scan(.wholeMac) }
                    Divider()
                    Button("Choose Folder…") { model.chooseFolder() }
                } label: {
                    Label(model.scopeTitle, systemImage: "internaldrive")
                        .labelStyle(.titleAndIcon)
                }
                .help("What to scan")
            }
            ToolbarItem {
                Picker("Minimum size", selection: $model.minSizeMB) {
                    ForEach(sizeOptions, id: \.self) { mb in
                        Text(mb >= 1024 ? "≥ \(mb / 1024) GB" : "≥ \(mb) MB").tag(mb)
                    }
                }
                .pickerStyle(.menu)
                .help("Only show items at least this big")
            }
            ToolbarItem {
                if model.scanning {
                    Button { model.stop() } label: { Label("Stop", systemImage: "stop.fill") }
                        .help("Stop and show what was found so far")
                } else {
                    Button { model.startScan() } label: { Label("Scan", systemImage: "arrow.clockwise") }
                        .keyboardShortcut("r")
                        .help("Scan again (⌘R)")
                }
            }
        }
    }

    private var table: some View {
        Table(model.shown, children: \.children, selection: $model.selection, sortOrder: $model.sortOrder) {
            TableColumn("Name", value: \.name) { row in
                HStack(spacing: 6) {
                    if let p = row.iconPath {
                        Image(nsImage: IconCache.icon(p))
                            .resizable()
                            .frame(width: 16, height: 16)
                    } else {
                        Image(systemName: row.children == nil ? "doc" : "folder")
                            .frame(width: 16, height: 16)
                            .foregroundStyle(.secondary)
                    }
                    Text(row.name).lineLimit(1).fontWeight(row.children == nil ? .regular : .semibold)
                }
            }
            .width(min: 180, ideal: 300)

            TableColumn("Size", value: \.size) { row in
                Text(fmt(row.size))
                    .monospacedDigit()
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .width(min: 70, ideal: 90, max: 130)

            TableColumn("Kind", value: \.kindLabel)
                .width(min: 50, ideal: 60, max: 90)

            TableColumn("Location", value: \.location) { row in
                Text(row.location)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .help(row.location)
            }
        }
        .contextMenu(forSelectionType: String.self) { ids in
            if !ids.isEmpty {
                Button("Reveal in Finder") { model.reveal(ids) }
                Button("Open") { model.open(ids) }
                Button("Copy Path") { model.copyPaths(ids) }
                Divider()
                Button("Move to Trash…", role: .destructive) { model.trash(ids) }
            }
        } primaryAction: { ids in
            model.reveal(ids)
        }
        .onDeleteCommand { model.trash(model.selection) }
    }

    private var emptyState: some View {
        VStack(spacing: 14) {
            Image(systemName: "internaldrive")
                .font(.system(size: 46))
                .foregroundStyle(.secondary)
            Text("Find what’s taking up space")
                .font(.title2.bold())
            Text("Shows what takes space, grouped by the app it belongs to. Expand an app to see its biggest files.")
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
            HStack(spacing: 10) {
                Button("Scan Apps & Home") { model.scan(.home) }
                    .keyboardShortcut(.defaultAction)
                Button("Scan Whole Mac") { model.scan(.wholeMac) }
                Button("Choose Folder…") { model.chooseFolder() }
            }
            .controlSize(.large)
            .padding(.top, 6)
        }
        .padding(40)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var statusBar: some View {
        HStack(spacing: 8) {
            if model.scanning {
                ProgressView().controlSize(.small)
                Text("Scanning… \(model.files.formatted()) files · \(fmt(model.bytes))")
                    .monospacedDigit()
                Text(Item.pretty(model.currentPath))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            } else if model.hasScanned {
                Text("\(model.shown.count.formatted()) apps & folders · \(fmt(model.shownBytes))")
                Text("of \(model.files.formatted()) files (\(fmt(model.bytes))) · \(String(format: "%.1f", model.elapsed)) s")
                    .foregroundStyle(.secondary)
                if model.errors > 0 {
                    Button("\(model.errors.formatted()) locations couldn’t be read — grant Full Disk Access") {
                        model.openFullDiskAccess()
                    }
                    .buttonStyle(.link)
                }
            } else {
                Text("Ready").foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
        .font(.callout)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

@main
struct BigFilesApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var appDelegate
    @StateObject private var model = Model()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(model)
                .frame(minWidth: 820, minHeight: 480)
        }
        .commands { CommandGroup(replacing: .newItem) {} }
    }
}
