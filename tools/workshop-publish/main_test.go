//go:build windows

package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestStageContentSkipsRuntimeJunk(t *testing.T) {
	src, dst := t.TempDir(), t.TempDir()
	keep := []string{".workshop", "Mod.dll", "mod.rules", "assets/a.png", "README.md"}
	junk := []string{"Mod.log", "SharedShip.LOG", "Autofleet.state.json", "Autofleet.saves/x.json", "Autofleet.profiles.json.bad", "MiningDrone.settings.json"}
	for _, f := range append(keep, junk...) {
		p := filepath.Join(src, f)
		os.MkdirAll(filepath.Dir(p), 0o755)
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
