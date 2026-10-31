package main

import (
	"fmt"
	"image"
	"image/color"
	"path/filepath"
	"strings"
)

// §28 joint matrix: every linked ordered pair (source output -> receiver input) of belt parts and boxes, composed from
// the baked assets exactly as the game layers them (rules floor, C# surface / edge quads with the C# flipU rules), in
// every world direction, both part flips and all 16 surface frames; seam profiles across the shared edge are compared.

// jpart is one part instance in its displayed frame (C# flipU applied), front = up before rotation.
type jpart struct {
	name           string
	img, ref, mask *image.NRGBA // art, floor reference (floor pixel the art was baked over), belt surface band mask
	w              int          // tiles wide (all parts are 1 tile tall in the displayed frame)
	port           image.Point  // port cell (displayed frame)
	dir            int          // port outward direction (displayed frame)
	side           bool         // receiver entered at a belt side (join / curve): rails end there by design
	zone           int          // box port zone depth (whole depth checked), 0 = belt part
}

type jassets struct {
	game, assets string
	floor        *image.NRGBA
	band         *image.NRGBA // straight surface band mask (64 px)
	cache        map[string]*image.NRGBA
}

func newJAssets(game, assets string) *jassets {
	a := &jassets{game: game, assets: assets, cache: map[string]*image.NRGBA{}}
	a.floor = resize(load(filepath.Join(game, "Data", "ships", "terran", "corridor", "floor.png")), 64, 64)
	a.band = image.NewNRGBA(image.Rect(0, 0, 64, 64))
	for y := range 64 {
		for x := 15; x < 49; x++ {
			a.band.SetNRGBA(x, y, color.NRGBA{255, 255, 255, 255})
		}
	}
	return a
}

func (a *jassets) get(rel string) *image.NRGBA {
	if im, ok := a.cache[rel]; ok {
		return im
	}
	im := load(filepath.Join(a.assets, rel))
	a.cache[rel] = im
	return im
}

func tiled(t *image.NRGBA, w int) *image.NRGBA {
	out := image.NewNRGBA(image.Rect(0, 0, w*64, 64))
	for x := range w {
		paste(out, t, x*64, 0)
	}
	return out
}

func clone(im *image.NRGBA) *image.NRGBA {
	o := image.NewNRGBA(im.Rect)
	copy(o.Pix, im.Pix)
	return o
}

func flipIf(im *image.NRGBA, f bool) *image.NRGBA {
	if f {
		return flipH(im)
	}
	return im
}

// bandMask of a belt surface variant in its sprite frame (same remaps as the surface itself).
func (a *jassets) bandMask(v string) *image.NRGBA {
	switch {
	case v == "curve":
		return curveLeft(a.band)
	case strings.HasSuffix(v, "_crossing"):
		return rotateCW(a.band)
	case v == "join_l" || v == "join_r" || v == "join_lr":
		m := clone(a.band)
		for y := 15; y < 49; y++ {
			for x := range 64 {
				if (x < 15 && v != "join_r") || (x >= 49 && v != "join_l") {
					m.SetNRGBA(x, y, color.NRGBA{255, 255, 255, 255})
				}
			}
		}
		return m
	}
	return a.band
}

// belt: surface variant v (C# Select flip sel) of a belt with part flip pf at frame k: quad flipU = sel ^ pf.
func (a *jassets) belt(v string, sel, pf bool, k, lvl int) jpart {
	f := sel != pf
	img := a.get(fmt.Sprintf("belt_anim/%s%s_%02d.png", v, damageSuffix[lvl], k))
	return jpart{name: v, img: flipIf(img, f), ref: flipIf(a.floor, f), mask: flipIf(a.bandMask(v), f), w: 1}
}

// box: rules floor (drawn with the part flip) under the edge quads (drawn unflipped, chosen by displayed position).
func (a *jassets) box(bi int, pf bool, looks []int, lvl int) jpart {
	b := boxes[bi]
	img := clone(flipIf(a.get("belt_"+b.name+damageSuffix[lvl]+".png"), pf))
	ref := flipIf(tiled(a.floor, b.w), pf)
	mask := image.NewNRGBA(img.Rect)
	for e, l := range looks {
		if l < 0 {
			continue
		}
		paste(img, a.get("belt_edge/"+edgeFile(b, e, l, lvl)), 0, 0)
		z, band := b.zones[e], rot(a.band, beltRot[b.out[e]])
		for y := z.Min.Y; y < z.Max.Y; y++ {
			for x := z.Min.X; x < z.Max.X; x++ {
				ref.SetNRGBA(x, y, color.NRGBA{}) // the opaque quad covers the floor there
				if l == lookBelt {
					mask.SetNRGBA(x, y, band.NRGBAAt(x%64, y))
				}
			}
		}
	}
	return jpart{name: b.name, img: img, ref: ref, mask: mask, w: b.w}
}

// defaultLooks: every port closed (Cap), splitter side walls plain (-1), except edge e with look l.
func defaultLooks(bi, e, l int) []int {
	looks := make([]int, len(boxes[bi].zones))
	for i := range looks {
		looks[i] = lookCap
		if i >= 4 {
			looks[i] = -1
		}
	}
	looks[e] = l
	return looks
}

type jcase struct {
	src, dst jpart
	world    int // world direction of the joint seen from src
}

// place: rotation turning the port dir to world, and the port cell after rotation (part bounding box origin 0,0).
func place(p jpart, world int) (r int, cell image.Point) {
	r = (world - p.dir + 4) % 4
	x, y, w, h := p.port.X, p.port.Y, p.w, 1
	for range r {
		x, y, w, h = h-1-y, x, h, w
	}
	return r, image.Pt(x, y)
}

// pixel of p rotated r times at (x, y) of its rotated bounding box.
func pixel(im *image.NRGBA, r, x, y int) color.NRGBA {
	w, h := im.Rect.Dx(), im.Rect.Dy()
	if r%2 == 1 {
		w, h = h, w
	}
	for range r {
		// undo one CW turn: out(x, y) = in(y, inH-1-x), inH = current width
		x, y, w, h = y, w-1-x, h, w
	}
	return im.NRGBAAt(x, y)
}

// profile: classes along the port edge of p (rotated so the port faces world) at depth t into the part, in world
// order (left -> right, top -> bottom): 0 floor, 1 structure, 2 belt surface.
func profile(p jpart, world, t int) []int {
	r, c := place(p, world)
	out := make([]int, 64)
	for i := range 64 {
		var x, y int
		switch world {
		case 0:
			x, y = c.X*64+i, c.Y*64+t
		case 2:
			x, y = c.X*64+i, c.Y*64+63-t
		case 1:
			x, y = c.X*64+63-t, c.Y*64+i
		default:
			x, y = c.X*64+t, c.Y*64+i
		}
		px, ref := pixel(p.img, r, x, y), pixel(p.ref, r, x, y)
		switch {
		case px == ref || px.A == 0:
			out[i] = 0
		case pixel(p.mask, r, x, y).A >= 128:
			out[i] = 2
		default:
			out[i] = 1
		}
	}
	return out
}

// runs: [lo, hi) of the non-floor extent and of the surface band (-1 when absent).
func runs(pr []int) (lo, hi, slo, shi int) {
	lo, hi, slo, shi = -1, -1, -1, -1
	for i, c := range pr {
		if c != 0 {
			if lo < 0 {
				lo = i
			}
			hi = i + 1
		}
		if c == 2 {
			if slo < 0 {
				slo = i
			}
			shi = i + 1
		}
	}
	return
}

func near(a, b int) bool { return a-b <= 1 && b-a <= 1 }

// seam checks one joint: "" = mates, else why not.
func seam(c jcase) string {
	back := (c.world + 2) % 4
	a := profile(c.src, c.world, 0)
	b := profile(c.dst, back, 0)
	lo1, hi1, sLo1, sHi1 := runs(a)
	lo2, hi2, sLo2, sHi2 := runs(b)
	switch {
	case lo1 < 0 || lo2 < 0:
		return fmt.Sprintf("gap: nothing at the edge (src %d..%d, dst %d..%d)", lo1, hi1, lo2, hi2)
	case (sLo1 < 0) != (sLo2 < 0):
		return fmt.Sprintf("surface on one side only (src %d..%d, dst %d..%d)", sLo1, sHi1, sLo2, sHi2)
	case sLo1 >= 0 && (!near(sLo1, sLo2) || !near(sHi1, sHi2)):
		return fmt.Sprintf("surface offset (src %d..%d, dst %d..%d)", sLo1, sHi1, sLo2, sHi2)
	case c.dst.side && (lo2 < lo1-1 || hi2 > hi1+1):
		return fmt.Sprintf("side opening wider than the feeder (src %d..%d, dst %d..%d)", lo1, hi1, lo2, hi2)
	case !c.dst.side && (!near(lo1, lo2) || !near(hi1, hi2)):
		return fmt.Sprintf("rails offset (src %d..%d, dst %d..%d)", lo1, hi1, lo2, hi2)
	}
	// Box ports: the whole zone depth keeps the seam profile (no old stub, hazard strip or gap behind the edge).
	for _, s := range []struct {
		p     jpart
		world int
		lo    int
	}{{c.src, c.world, lo1}, {c.dst, back, lo2}} {
		_, hi, _, _ := runs(profile(s.p, s.world, 0))
		for t := 1; t < s.p.zone; t++ {
			pr := profile(s.p, s.world, t)
			for i := s.lo + 1; i < hi-1; i++ {
				if pr[i] == 0 {
					return fmt.Sprintf("%s: gap at %d, %d px behind the edge", s.p.name, i, t)
				}
			}
		}
	}
	return ""
}

// beltSrc: every belt-family output (front edge) at frame k: surface variants (C# Select flip), crossings (5 arts).
type jgen struct {
	name string
	make func(pf bool, k, lvl int, peerBox bool) jpart
}

var crossArts = []string{"heat_crossing", "cable_crossing", "plain_crossing", "heat_cable_crossing", "cable_heat_crossing"}

func (a *jassets) sources() []jgen {
	var g []jgen
	for _, v := range []struct {
		v   string
		sel bool
	}{{"straight", false}, {"join_l", false}, {"join_r", false}, {"join_lr", false}, {"curve", false}, {"curve", true}} {
		name := v.v + map[bool]string{true: "R"}[v.sel]
		g = append(g, jgen{name, func(pf bool, k, lvl int, _ bool) jpart { p := a.belt(v.v, v.sel, pf, k, lvl); p.name = name; return p }})
	}
	for _, c := range crossArts {
		g = append(g, jgen{c, func(pf bool, k, lvl int, _ bool) jpart {
			p := a.belt(c, false, pf, k, lvl)
			p.dir = map[bool]int{false: 1, true: 3}[pf] // flows right in the part frame
			return p
		}})
	}
	look := func(peerBox bool) int { return map[bool]int{false: lookBelt, true: lookCoupling}[peerBox] }
	g = append(g, jgen{"connector>", func(pf bool, _, lvl int, peerBox bool) jpart {
		p := a.box(0, pf, defaultLooks(0, 0, look(peerBox)), lvl)
		p.zone = boxes[0].zones[0].Dy()
		return p
	}})
	for h := range 2 {
		g = append(g, jgen{fmt.Sprintf("splitter%d>", h), func(pf bool, _, lvl int, peerBox bool) jpart {
			c := h
			if pf {
				c = 1 - h
			}
			p := a.box(1, pf, defaultLooks(1, c, look(peerBox)), lvl)
			p.port, p.zone = image.Pt(c, 0), boxes[1].zones[c].Dy()
			return p
		}})
	}
	return g
}

// receivers: rear inputs, side inputs (joins / curves / crossing and splitter sides), box intakes.
func (a *jassets) receivers() []jgen {
	var g []jgen
	for _, v := range []string{"straight", "join_l", "join_r", "join_lr"} {
		g = append(g, jgen{">" + v, func(pf bool, k, lvl int, _ bool) jpart { p := a.belt(v, false, pf, k, lvl); p.dir = 2; return p }})
	}
	// Side entries: the side is topology (left / right of travel), the art follows the C# Select flip ^ part flip.
	for _, v := range []struct {
		name, v string
		sel     bool
		dir     int
	}{{">join_l(L)", "join_l", false, 3}, {">join_r(R)", "join_r", false, 1}, {">join_lr(L)", "join_lr", false, 3}, {">join_lr(R)", "join_lr", false, 1}, {">curve(L)", "curve", false, 3}, {">curveR(R)", "curve", true, 1}} {
		g = append(g, jgen{v.name, func(pf bool, k, lvl int, _ bool) jpart {
			p := a.belt(v.v, v.sel, pf, k, lvl)
			p.dir, p.side = v.dir, true
			return p
		}})
	}
	for _, c := range crossArts {
		g = append(g, jgen{">" + c, func(pf bool, k, lvl int, _ bool) jpart {
			p := a.belt(c, false, pf, k, lvl)
			p.dir = map[bool]int{false: 3, true: 1}[pf]
			return p
		}})
		// Side load into a crossing (sim allows it, NoCurve): enters over its heat / cable port side.
		g = append(g, jgen{">" + c + "(side)", func(pf bool, k, lvl int, _ bool) jpart {
			p := a.belt(c, false, pf, k, lvl)
			p.dir, p.side = 0, true
			return p
		}})
	}
	look := func(peerBox bool) int { return map[bool]int{false: lookBelt, true: lookCoupling}[peerBox] }
	for _, e := range []int{2, 3, 1} {
		g = append(g, jgen{fmt.Sprintf(">connector%s", []string{"", "R", "B", "L"}[e]), func(pf bool, _, lvl int, peerBox bool) jpart {
			p := a.box(0, pf, defaultLooks(0, e, look(peerBox)), lvl)
			p.dir = boxes[0].out[e]
			z := boxes[0].zones[e]
			p.zone = min(z.Dx(), z.Dy())
			return p
		}})
	}
	for _, e := range []int{2, 3, 4, 5} {
		g = append(g, jgen{fmt.Sprintf(">splitter%s", []string{"", "", "in0", "in1", "sideL", "sideR"}[e]), func(pf bool, _, lvl int, peerBox bool) jpart {
			c := e
			if pf { // displayed edge of the same half / side
				c = map[int]int{2: 3, 3: 2, 4: 5, 5: 4}[e]
			}
			p := a.box(1, pf, defaultLooks(1, c, look(peerBox)), lvl)
			z := boxes[1].zones[c]
			p.dir, p.port, p.zone = boxes[1].out[c], image.Pt(z.Min.X/64, 0), min(z.Dx(), z.Dy())
			return p
		}})
	}
	return g
}

func isBox(name string) bool {
	return strings.Contains(name, "connector") || strings.Contains(name, "splitter")
}

// flips: belts and crossings are IsFlippable = false (mod/belt.rules, belt_crossing.rules); boxes are checked flipped
// too although they inherit it (the edge quads are flip-independent by construction).
func flips(name string) []bool {
	if isBox(name) {
		return []bool{false, true}
	}
	return []bool{false}
}

// jointResult: one ordered pair over all transforms; fails lists the first failing transform per reason.
type jointResult struct {
	src, dst string
	n        int
	fails    []string
}

// joints runs every pair x 4 world directions x both flips x frames (0..frames-1) at damage level 0 (levels keep the
// tile border by construction, checked by TestEdgeDamageSeamless / TestDamageSeamless).
func joints(a *jassets, frames int) []jointResult {
	var out []jointResult
	for _, s := range a.sources() {
		for _, d := range a.receivers() {
			r := jointResult{src: s.name, dst: d.name}
			seen := map[string]bool{}
			for world := range 4 {
				for _, fs := range flips(s.name) {
					for _, fd := range flips(d.name) {
						for k := range frames {
							c := jcase{s.make(fs, k, 0, isBox(d.name)), d.make(fd, k, 0, isBox(s.name)), world}
							r.n++
							if why := seam(c); why != "" {
								key, _, _ := strings.Cut(why, " (")
								if !seen[key] {
									seen[key] = true
									r.fails = append(r.fails, fmt.Sprintf("world %d flip %v/%v frame %d: %s", world, fs, fd, k, why))
								}
							}
						}
					}
				}
			}
			out = append(out, r)
		}
	}
	return out
}
