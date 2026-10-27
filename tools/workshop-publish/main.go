//go:build windows

// workshop-publish creates or updates a Cosmoteer Steam Workshop item with
// per-language titles/descriptions, via the logged-in Steam client.
// It mirrors Cosmoteer.Steamworks.WorkshopUploaderDialog: same ".workshop"
// id file in the content folder, tag "Mods", content + preview + visibility.
package main

import (
	"bufio"
	"flag"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"syscall"
)

const workshopFile = ".workshop" // WorkshopUploaderDialog.WORKSHOP_FILE

// Steam API language codes (Steamworks "Languages Supported on Steam").
var steamLangs = map[string]bool{
	"arabic": true, "bulgarian": true, "schinese": true, "tchinese": true, "czech": true,
	"danish": true, "dutch": true, "english": true, "finnish": true, "french": true,
	"german": true, "greek": true, "hungarian": true, "indonesian": true, "italian": true,
	"japanese": true, "koreana": true, "norwegian": true, "polish": true, "portuguese": true,
	"brazilian": true, "romanian": true, "russian": true, "spanish": true, "latam": true,
	"swedish": true, "thai": true, "turkish": true, "ukrainian": true, "vietnamese": true,
}

// ERemoteStoragePublishedFileVisibility.
var visibilities = map[string]int{"public": 0, "friends": 1, "private": 2, "hidden": 3 /* Unlisted */}

type text struct{ lang, title, desc string }

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "error:", err)
		os.Exit(1)
	}
}

func run() error {
	dll := flag.String("dll", `D:\Steam\steamapps\common\Cosmoteer\Bin\steam_api64.dll`, "path to steam_api64.dll")
	var appIDFlag uint32
	flag.Func("appid", "Steam AppID (default: SteamAppId env, steam_appid.txt next to exe, else 799600)", func(s string) error {
		v, err := strconv.ParseUint(s, 10, 32)
		if err != nil {
			return err
		}
		appIDFlag = uint32(v)
		return nil
	})
	content := flag.String("content", "", "installed mod folder (uploaded as item content; holds .workshop)")
	preview := flag.String("preview", "", "preview image (jpg/png/gif)")
	textsDir := flag.String("texts", "", "dir with <steamlang>.txt: first line title, rest description; english.txt required")
	visFlag := flag.String("visibility", "", "public|friends|private|hidden (omitted = private on create, left unchanged on update)")
	changeNote := flag.String("changenote", "", "change note for the content update")
	minVer := flag.String("min-version", "", "required min game branch version, e.g. 0.30.0 (SetRequiredGameVersions; set only if min or max given)")
	maxVer := flag.String("max-version", "", "required max game branch version")
	dryRun := flag.Bool("dry-run", false, "init Steam and validate only; never create or submit")
	flag.Parse()

	if *content == "" || *textsDir == "" {
		flag.Usage()
		return fmt.Errorf("-content and -texts are required")
	}
	absContent, err := filepath.Abs(*content)
	if err != nil {
		return err
	}
	if st, err := os.Stat(absContent); err != nil || !st.IsDir() {
		return fmt.Errorf("content folder not found: %s", absContent)
	}
	var absPreview string
	if *preview != "" {
		if absPreview, err = filepath.Abs(*preview); err != nil {
			return err
		}
		st, err := os.Stat(absPreview)
		if err != nil || st.IsDir() {
			return fmt.Errorf("preview not found: %s", absPreview)
		}
		if st.Size() >= 1<<20 {
			fmt.Printf("warning: preview is %d bytes; Steam recommends < 1 MB\n", st.Size())
		}
	}
	texts, err := loadTexts(*textsDir)
	if err != nil {
		return err
	}
	fileID, err := readWorkshopID(absContent)
	if err != nil {
		return err
	}
	vis, err := visibilityFor(*visFlag, fileID == 0)
	if err != nil {
		return err
	}
	appID := resolveAppID(appIDFlag)

	// steam_api reads SteamAppId from the environment; set it so no steam_appid.txt is needed.
	for _, k := range []string{"SteamAppId", "SteamGameId"} {
		if err := os.Setenv(k, strconv.FormatUint(uint64(appID), 10)); err != nil {
			return fmt.Errorf("set %s: %w", k, err)
		}
	}

	s, err := loadSteam(*dll)
	if err != nil {
		return err
	}
	if err := s.init(); err != nil {
		return fmt.Errorf("%w (is the Steam client running and logged in?)", err)
	}
	defer s.shutdown()
	fmt.Printf("steam: appid=%d loggedOn=%v steamID=%d persona=%q\n", appID, s.loggedOn(), s.steamID(), s.persona())
	fmt.Printf("content: %s\npreview: %s\nvisibility: %d (-1 = unchanged)\n", absContent, orNone(absPreview), vis)
	for _, t := range texts {
		fmt.Printf("text %-10s title=%q (%d B) desc=%d B\n", t.lang, t.title, len(t.title), len(t.desc))
	}
	if fileID != 0 {
		fmt.Printf("item: update %d (from %s)\n", fileID, filepath.Join(absContent, workshopFile))
	} else {
		fmt.Println("item: none yet -> CreateItem + write", filepath.Join(absContent, workshopFile))
	}
	// Upload a filtered copy: mods write logs/state into their installed folder.
	stage, err := os.MkdirTemp("", "workshop-publish-")
	if err != nil {
		return err
	}
	defer func() {
		if err := os.RemoveAll(stage); err != nil {
			fmt.Printf("warning: could not remove staging dir %s: %v\n", stage, err)
		}
	}()
	n, skipped, err := stageContent(absContent, stage)
	if err != nil {
		return fmt.Errorf("staging content: %w", err)
	}
	fmt.Printf("staged %d files (%s), skipped %v\n", n, stage, skipped)
	if *dryRun {
		fmt.Println("dry-run: inputs valid; nothing created or submitted")
		return nil
	}
	if !s.loggedOn() {
		return fmt.Errorf("steam user not logged on")
	}

	needsAgreement := false
	if fileID == 0 {
		res, err := s.createItem(appID)
		if err != nil {
			return fmt.Errorf("CreateItem: %w", err)
		}
		fileID = res.FileID
		needsAgreement = needsAgreement || res.NeedsAgreement
		if err := writeWorkshopID(absContent, fileID); err != nil {
			return fmt.Errorf("item %d created but writing .workshop failed: %w", fileID, err)
		}
		fmt.Printf("created item %d\n", fileID)
	}

	// Main update: content, preview, tags, visibility, versions, english (default) text.
	u, err := s.startItemUpdate(appID, fileID)
	if err != nil {
		return err
	}
	en := texts[0]
	steps := []func() error{
		func() error { return u.title(en.title) },
		func() error { return u.desc(en.desc) },
		func() error { return u.tags([]string{"Mods"}) },
		func() error { return u.content(stage) },
	}
	if vis >= 0 {
		steps = append(steps, func() error { return u.visibility(vis) })
	}
	if absPreview != "" {
		steps = append(steps, func() error { return u.preview(absPreview) })
	}
	if *minVer != "" || *maxVer != "" {
		steps = append(steps, func() error { return u.gameVersions(*minVer, *maxVer) })
	}
	for _, f := range steps {
		if err := f(); err != nil {
			return err
		}
	}
	fmt.Println("submitting content + english ...")
	res, err := u.submit(*changeNote)
	if err != nil {
		return err
	}
	needsAgreement = needsAgreement || res.NeedsAgreement

	for _, t := range texts[1:] {
		u, err := s.startItemUpdate(appID, fileID)
		if err != nil {
			return err
		}
		for _, f := range []func() error{
			func() error { return u.lang(t.lang) },
			func() error { return u.title(t.title) },
			func() error { return u.desc(t.desc) },
		} {
			if err := f(); err != nil {
				return fmt.Errorf("%s: %w", t.lang, err)
			}
		}
		fmt.Printf("submitting %s ...\n", t.lang)
		res, err := u.submit("")
		if err != nil {
			return fmt.Errorf("%s: %w", t.lang, err)
		}
		needsAgreement = needsAgreement || res.NeedsAgreement
	}

	fmt.Printf("item %d\nhttps://steamcommunity.com/sharedfiles/filedetails/?id=%d\n", fileID, fileID)
	fmt.Printf("userNeedsToAcceptWorkshopLegalAgreement=%v\n", needsAgreement)
	if needsAgreement {
		fmt.Println("accept at https://steamcommunity.com/sharedfiles/workshoplegalagreement")
	}
	return nil
}

// visibilityFor returns the visibility to set, or -1 to leave it unchanged: an explicit
// -visibility always applies; omitted, a new item is private and an existing one keeps its visibility.
func visibilityFor(flagVal string, create bool) (int, error) {
	if flagVal == "" {
		if create {
			return visibilities["private"], nil
		}
		return -1, nil
	}
	v, ok := visibilities[flagVal]
	if !ok {
		return 0, fmt.Errorf("bad -visibility %q", flagVal)
	}
	return v, nil
}

func orNone(s string) string {
	if s == "" {
		return "(unchanged)"
	}
	return s
}

// loadTexts returns english first, then other languages sorted.
func loadTexts(dir string) ([]text, error) {
	fsys := os.DirFS(dir)
	entries, err := fs.ReadDir(fsys, ".")
	if err != nil {
		return nil, err
	}
	var out []text
	for _, e := range entries {
		if e.IsDir() || !strings.EqualFold(filepath.Ext(e.Name()), ".txt") {
			continue
		}
		lang := strings.ToLower(strings.TrimSuffix(e.Name(), filepath.Ext(e.Name())))
		if !steamLangs[lang] {
			return nil, fmt.Errorf("%s: %q is not a Steam API language code", e.Name(), lang)
		}
		raw, err := fs.ReadFile(fsys, e.Name())
		if err != nil {
			return nil, err
		}
		s := strings.ReplaceAll(strings.TrimPrefix(string(raw), "\uFEFF"), "\r\n", "\n")
		title, desc, _ := strings.Cut(s, "\n")
		title, desc = strings.TrimSpace(title), strings.Trim(desc, "\n ")
		switch {
		case title == "":
			return nil, fmt.Errorf("%s: empty title", e.Name())
		case len(title) >= kCchPublishedDocumentTitleMax:
			return nil, fmt.Errorf("%s: title %d bytes, max %d", e.Name(), len(title), kCchPublishedDocumentTitleMax-1)
		case len(desc) >= kCchPublishedDocumentDescriptionMax:
			return nil, fmt.Errorf("%s: description %d bytes UTF-8, max %d (k_cchPublishedDocumentDescriptionMax incl. NUL)", e.Name(), len(desc), kCchPublishedDocumentDescriptionMax-1)
		}
		out = append(out, text{lang, title, desc})
	}
	sort.Slice(out, func(i, j int) bool {
		if (out[i].lang == "english") != (out[j].lang == "english") {
			return out[i].lang == "english"
		}
		return out[i].lang < out[j].lang
	})
	if len(out) == 0 || out[0].lang != "english" {
		return nil, fmt.Errorf("%s: english.txt required (default language)", dir)
	}
	return out, nil
}

// runtimeJunk: base-name patterns (case-insensitive) that mods write at runtime into
// their installed folder and must never ship. Autofleet's names come from MiningDrone Store.cs.
// .workshop is uploader-only (WorkshopUploaderDialog); subscribers' mods get their id from Steam (ModInfo GetItemInstallInfo).
var runtimeJunk = []string{
	workshopFile, "*.log", "*.tmp", "*.bak",
	"*.prefs.json", "*.state.json", "*.profiles.json", "*.settings.json", "*.saves",
}

func isRuntimeJunk(name string) bool {
	name = strings.ToLower(name)
	for _, p := range runtimeJunk {
		if ok, _ := filepath.Match(p, name); ok {
			return true
		}
	}
	return false
}

// stageContent copies src into dst, skipping runtimeJunk files and directories.
// Both trees are opened as os.Root so a symlink inside src cannot redirect a read or write outside them.
func stageContent(src, dst string) (n int, skipped []string, err error) {
	srcRoot, err := os.OpenRoot(src)
	if err != nil {
		return 0, nil, err
	}
	defer func() { _ = srcRoot.Close() }() // closes a directory handle only: nothing to lose
	dstRoot, err := os.OpenRoot(dst)
	if err != nil {
		return 0, nil, err
	}
	defer func() { _ = dstRoot.Close() }() // each WriteFile already closed its own file
	err = fs.WalkDir(srcRoot.FS(), ".", func(p string, d fs.DirEntry, err error) error {
		if err != nil || p == "." {
			return err
		}
		if isRuntimeJunk(d.Name()) {
			skipped = append(skipped, filepath.FromSlash(p))
			if d.IsDir() {
				return fs.SkipDir
			}
			return nil
		}
		if d.IsDir() {
			return dstRoot.MkdirAll(p, 0o755)
		}
		raw, err := srcRoot.ReadFile(p)
		if err != nil {
			return err
		}
		n++
		return dstRoot.WriteFile(p, raw, 0o644)
	})
	return n, skipped, err
}

// readWorkshopID mirrors ulong.Parse(File.ReadAllText(folder/.workshop)).
func readWorkshopID(folder string) (uint64, error) {
	raw, err := fs.ReadFile(os.DirFS(folder), workshopFile)
	if os.IsNotExist(err) {
		return 0, nil
	}
	if err != nil {
		return 0, err
	}
	id, err := strconv.ParseUint(strings.TrimSpace(strings.TrimPrefix(string(raw), "\uFEFF")), 10, 64)
	if err != nil {
		return 0, fmt.Errorf("bad %s: %w", workshopFile, err)
	}
	return id, nil
}

// writeWorkshopID mirrors File.WriteAllText(id) + FileAttributes.Hidden.
func writeWorkshopID(folder string, id uint64) error {
	p := filepath.Join(folder, workshopFile)
	if err := os.WriteFile(p, []byte(strconv.FormatUint(id, 10)), 0o644); err != nil {
		return err
	}
	pp, err := syscall.UTF16PtrFromString(p)
	if err != nil {
		return err
	}
	return syscall.SetFileAttributes(pp, syscall.FILE_ATTRIBUTE_HIDDEN)
}

func resolveAppID(flagVal uint32) uint32 {
	if flagVal != 0 {
		return flagVal
	}
	if v, err := strconv.ParseUint(os.Getenv("SteamAppId"), 10, 32); err == nil && v != 0 {
		return uint32(v)
	}
	if exe, err := os.Executable(); err == nil {
		if f, err := os.Open(filepath.Join(filepath.Dir(exe), "steam_appid.txt")); err == nil {
			defer func() { _ = f.Close() }() // read-only file: a close error loses nothing
			sc := bufio.NewScanner(f)
			if sc.Scan() {
				if v, err := strconv.ParseUint(strings.TrimSpace(sc.Text()), 10, 32); err == nil && v != 0 {
					return uint32(v)
				}
			}
		}
	}
	return 799600
}
