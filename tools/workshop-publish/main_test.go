//go:build windows

package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestVisibilityFor(t *testing.T) {
	for _, c := range []struct {
		flag   string
		create bool
		want   int
	}{
		{"", false, -1}, // update without -visibility: leave unchanged
		{"", true, 2},   // create without -visibility: private
		{"public", false, 0},
		{"hidden", true, 3},
	} {
		if got, err := visibilityFor(c.flag, c.create); err != nil || got != c.want {
			t.Errorf("visibilityFor(%q, %v) = %d, %v; want %d", c.flag, c.create, got, err, c.want)
		}
	}
	if _, err := visibilityFor("bogus", false); err == nil {
		t.Error("bogus visibility accepted")
	}
}

func TestStageContentSkipsRuntimeJunk(t *testing.T) {
	src, dst := t.TempDir(), t.TempDir()
	keep := []string{"Mod.dll", "mod.rules", "assets/a.png", "README.md"}
	junk := []string{".workshop", "Mod.log", "SharedShip.LOG", "Autofleet.state.json", "Autofleet.saves/x.json"}
	for _, f := range append(keep, junk...) {
		p := filepath.Join(src, f)
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte("x"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	n, _, err := stageContent(src, dst)
	if err != nil {
		t.Fatal(err)
	}
	if n != len(keep) {
		t.Errorf("staged %d files, want %d", n, len(keep))
	}
	for _, f := range keep {
		if _, err := os.Stat(filepath.Join(dst, f)); err != nil {
			t.Errorf("missing %s", f)
		}
	}
	for _, f := range junk {
		if _, err := os.Stat(filepath.Join(dst, f)); err == nil {
			t.Errorf("junk staged: %s", f)
		}
	}
}
