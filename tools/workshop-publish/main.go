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
	appIDFlag := flag.Uint("appid", 0, "Steam AppID (default: SteamAppId env, steam_appid.txt next to exe, else 799600)")
	content := flag.String("content", "", "installed mod folder (uploaded as item content; holds .workshop)")
	preview := flag.String("preview", "", "preview image (jpg/png/gif)")
	textsDir := flag.String("texts", "", "dir with <steamlang>.txt: first line title, rest description; english.txt required")
	visFlag := flag.String("visibility", "", "public|friends|private|hidden (empty = leave unchanged on update, public on create)")
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
	vis := -1
	if *visFlag != "" {
		v, ok := visibilities[*visFlag]
		if !ok {
			return fmt.Errorf("bad -visibility %q", *visFlag)
		}
		vis = v
	}
	texts, err := loadTexts(*textsDir)
	if err != nil {
		return err
	}
	fileID, err := readWorkshopID(absContent)
	if err != nil {
		return err
	}
	appID := resolveAppID(*appIDFlag)

	// steam_api reads SteamAppId from the environment; set it so no steam_appid.txt is needed.
	os.Setenv("SteamAppId", strconv.FormatUint(uint64(appID), 10))
	os.Setenv("SteamGameId", strconv.FormatUint(uint64(appID), 10))

	s, err := loadSteam(*dll)
	if err != nil {
		return err
	}
	if err := s.init(); err != nil {
		return fmt.Errorf("%w (is the Steam client running and logged in?)", err)
	}
	defer s.shutdown()
	fmt.Printf("steam: appid=%d loggedOn=%v steamID=%d persona=%q\n", appID, s.loggedOn(), s.steamID(), s.persona())
	fmt.Printf("content: %s\npreview: %s\n", absContent, orNone(absPreview))
	for _, t := range texts {
		fmt.Printf("text %-10s title=%q (%d B) desc=%d B\n", t.lang, t.title, len(t.title), len(t.desc))
	}
	if fileID != 0 {
		fmt.Printf("item: update %d (from %s)\n", fileID, filepath.Join(absContent, workshopFile))
	} else {
		fmt.Println("item: none yet -> CreateItem + write", filepath.Join(absContent, workshopFile))
	}
	if *dryRun {
		fmt.Println("dry-run: inputs valid; nothing created or submitted")
		return nil
	}
	if !s.loggedOn() {
		return fmt.Errorf("steam user not logged on")
	}

	needsAgreement := false
	if fileID == 0 {
		if vis < 0 {
			vis = 0
		}
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
		func() error { return u.content(absContent) },
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

func orNone(s string) string {
	if s == "" {
		return "(unchanged)"
	}
	return s
}

// loadTexts returns english first, then other languages sorted.
func loadTexts(dir string) ([]text, error) {
	entries, err := os.ReadDir(dir)
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
		raw, err := os.ReadFile(filepath.Join(dir, e.Name()))
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

// readWorkshopID mirrors ulong.Parse(File.ReadAllText(folder/.workshop)).
func readWorkshopID(folder string) (uint64, error) {
	raw, err := os.ReadFile(filepath.Join(folder, workshopFile))
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

func resolveAppID(flagVal uint) uint32 {
	if flagVal != 0 {
		return uint32(flagVal)
	}
	if v, err := strconv.ParseUint(os.Getenv("SteamAppId"), 10, 32); err == nil && v != 0 {
		return uint32(v)
	}
	if exe, err := os.Executable(); err == nil {
		if f, err := os.Open(filepath.Join(filepath.Dir(exe), "steam_appid.txt")); err == nil {
			defer f.Close()
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
