package main

import (
	"image"
	"image/color"
	"path/filepath"
	"strings"
	"testing"
)

func jointAssets(t *testing.T) *jassets {
	if !exists(game) {
		t.Skip("no game install")
	}
	return newJAssets(game, filepath.Join("..", "..", "mod", "assets"))
}

// §28 every linked pair mates across the shared edge (rails / surface within 1 px, no gap over a box port's whole
// depth) in 4 world directions, both box flips and all 16 surface frames. Known exception: side loading onto a crossing
// (its heat / cable port side has no belt opening art), which must keep failing until that art exists.
func TestJointsMate(t *testing.T) {
	res := joints(jointAssets(t), animFrames)
	n := 0
	for _, r := range res {
		n += r.n
		known := strings.HasSuffix(r.dst, "(side)") && strings.Contains(r.dst, "crossing")
		if known != (len(r.fails) > 0) {
			t.Errorf("%s -> %s: known=%v fails=%v", r.src, r.dst, known, r.fails)
		}
	}
	t.Logf("%d pairs, %d composed joints", len(res), n)
}

func sampleJoint(a *jassets, peer bool) jcase {
	for _, s := range a.sources() {
		if s.name == "connector>" {
			for _, d := range a.receivers() {
				if d.name == ">splitterin0" {
					return jcase{s.make(false, 0, 0, peer), d.make(false, 0, 0, peer), 0}
				}
			}
		}
	}
	panic("no sample")
}

// The seam check must catch the owner's defects: a 4 px port offset, a gap (full or partial) behind the edge.
func TestJointsNegative(t *testing.T) {
	a := jointAssets(t)
	for _, peer := range []bool{false, true} {
		c := sampleJoint(a, peer)
		if why := seam(c); why != "" {
			t.Fatalf("sample (peer box %v) should mate: %s", peer, why)
		}
		shift := c
		shift.dst.img, shift.dst.mask = clone(c.dst.img), clone(c.dst.mask)
		for y := 45; y < 64; y++ {
			for x := 63; x >= 4; x-- {
				shift.dst.img.SetNRGBA(x, y, c.dst.img.NRGBAAt(x-4, y))
				shift.dst.mask.SetNRGBA(x, y, c.dst.mask.NRGBAAt(x-4, y))
			}
		}
		gap := c
		gap.dst.img = clone(c.dst.img)
		for x := range 64 {
			gap.dst.img.SetNRGBA(x, 60, color.NRGBA{})
		}
		partial := c
		partial.dst.img = clone(c.dst.img)
		for x := 8; x < 55; x++ { // all but one pixel of a row behind the edge
			partial.dst.img.SetNRGBA(x, 60, color.NRGBA{})
		}
		for name, m := range map[string]jcase{"4 px shift": shift, "gap behind the edge": gap, "partial gap": partial} {
			if seam(m) == "" {
				t.Errorf("peer box %v: %s not detected", peer, name)
			}
		}
	}
}

// Edge quads are opaque over their whole zone (nothing of the fallback stub or hazard strip shows through) and keep the
// level-0 tile border at every damage level (joins to undamaged neighbours stay seamless).
func TestEdgeQuads(t *testing.T) {
	a := jointAssets(t)
	for _, b := range boxes {
		for e, z := range b.zones {
			for l := range edgeLooks {
				if !edgeHasLook(e, l) {
					continue
				}
				base := a.get("belt_edge/" + edgeFile(b, e, l, 0))
				for lvl := range damageSuffix {
					im := a.get("belt_edge/" + edgeFile(b, e, l, lvl))
					for y := range 64 {
						for x := range b.w * 64 {
							in := image.Pt(x, y).In(z)
							// A damaged belt stub keeps the damaged floor's holes in its 1 px floor margin (as the belt itself).
							c := im.NRGBAAt(x, y)
							hole := l == lookBelt && lvl > 0
							if in && c.A != 255 && !hole || !in && c.A != 0 {
								t.Fatalf("%s: (%d,%d) alpha %d, zone %v", edgeFile(b, e, l, lvl), x, y, c.A, z)
							}
							edge := x%64 == 0 || x%64 == 63 || y == 0 || y == 63
							if edge && im.NRGBAAt(x, y) != base.NRGBAAt(x, y) {
								t.Fatalf("%s: border (%d,%d) differs from level 0", edgeFile(b, e, l, lvl), x, y)
							}
						}
					}
				}
			}
		}
	}
}
