package main

import (
	"bytes"
	"fmt"
	"image"
	"image/color"
	"path/filepath"
	"testing"
)

const game = `D:\Steam\steamapps\common\Cosmoteer`

func flat(c color.NRGBA) *image.NRGBA {
	im := image.NewNRGBA(image.Rect(0, 0, 64, 64))
	for i := 0; i < len(im.Pix); i += 4 {
		im.Pix[i], im.Pix[i+1], im.Pix[i+2], im.Pix[i+3] = c.R, c.G, c.B, c.A
	}
	return im
}

// synthetic: real belt art and decals, flat floors (no game install needed).
func synthetic() (belt *image.NRGBA, surf, floors, decals []*image.NRGBA) {
	belt = load(filepath.Join("art", "belt.png"))
	surf = []*image.NRGBA{nil}
	decals = []*image.NRGBA{nil}
	for _, sfx := range damageSuffix {
		floors = append(floors, flat(color.NRGBA{176, 176, 176, 255}))
		if sfx != "" {
			d := load(filepath.Join("art", "belt_damage"+sfx+".png"))
			surf = append(surf, d)
			decals = append(decals, resize(d, 64, 64))
		}
	}
	return belt, surf, floors, decals
}

// Level 0 must stay the pre-§26-scroll bake: compare with the committed mod/assets/belt_anim frames.
func TestLevel0Unchanged(t *testing.T) {
	if !exists(game) {
		t.Skip("no game install")
	}
	floors, decals := loadDamage(game, "art")
	belt, surf, _, _ := synthetic()
	for _, v := range animVariants("art") {
		for k := range animFrames {
			got := animFrame(belt, surf, v, k, floors, decals)[0]
			want := load(filepath.Join("..", "..", "mod", "assets", "belt_anim", fmt.Sprintf("%s_%02d.png", v.name, k)))
			if !bytes.Equal(got.Pix, want.Pix) {
				t.Fatalf("%s frame %d: level 0 changed", v.name, k)
			}
		}
	}
}

// Damaged surface pixels move by the scroll step (4 px per frame) with the texture, inside the fully faded-in rows.
func TestSurfaceDamageScrolls(t *testing.T) {
	belt, surf, floors, decals := synthetic()
	v := animVariants("art")[0] // straight
	const step = 64 / animFrames
	for lvl := 1; lvl < len(damageSuffix); lvl++ {
		damaged := 0
		for k := range animFrames {
			a := animFrame(belt, surf, v, k, floors, decals)
			b := animFrame(belt, surf, v, (k+1)%animFrames, floors, decals)
			for y := 8; y+step <= 55; y++ {
				for x := 15; x < 49; x++ {
					if b[lvl].NRGBAAt(x, y) != a[lvl].NRGBAAt(x, y+step) {
						t.Fatalf("level %d frame %d: (%d,%d) did not scroll", lvl, k, x, y)
					}
					if a[lvl].NRGBAAt(x, y) != a[0].NRGBAAt(x, y) {
						damaged++
					}
				}
			}
		}
		if damaged == 0 {
			t.Fatalf("level %d: no damage on the surface", lvl)
		}
	}
}

// Every variant's damaged tile border equals the undamaged one: joins to undamaged neighbours stay seamless.
func TestDamageSeamless(t *testing.T) {
	belt, surf, floors, decals := synthetic()
	for _, v := range animVariants("art") {
		for k := range animFrames {
			fr := animFrame(belt, surf, v, k, floors, decals)
			for lvl := 1; lvl < len(fr); lvl++ {
				for i := range 64 {
					for _, p := range []image.Point{{i, 0}, {i, 63}, {0, i}, {63, i}} {
						if fr[lvl].NRGBAAt(p.X, p.Y) != fr[0].NRGBAAt(p.X, p.Y) {
							t.Fatalf("%s level %d frame %d: edge %v differs", v.name, lvl, k, p)
						}
					}
				}
			}
		}
	}
}
