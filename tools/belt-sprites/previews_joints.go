package main

import (
	"fmt"
	"image"
	"image/color"
	"os"
	"path/filepath"
	"strings"
)

// scenePart: a displayed part image rotated r quarter turns with its rotated bounding box at tile (tx, ty).
type scenePart struct {
	im        *image.NRGBA
	tx, ty, r int
}

func (a *jassets) room() *image.NRGBA {
	room := load(filepath.Join(a.game, "Data", "ships", "terran", "storage_2x2", "floor.png"))
	return resize(crop(room, image.Rect(0, 0, room.Rect.Dx()/2, room.Rect.Dy()/2)), 64, 64)
}

func (a *jassets) scene(w, h int, parts []scenePart) *image.NRGBA {
	out := image.NewNRGBA(image.Rect(0, 0, w*64, h*64))
	for ty := range h {
		for tx := range w {
			paste(out, a.floor, tx*64, ty*64)
		}
	}
	for _, p := range parts {
		paste(out, rot(p.im, p.r), p.tx*64, p.ty*64)
	}
	return out
}

func fill(im *image.NRGBA, c color.NRGBA) {
	for i := 0; i < len(im.Pix); i += 4 {
		im.Pix[i], im.Pix[i+1], im.Pix[i+2], im.Pix[i+3] = c.R, c.G, c.B, c.A
	}
}

// portLayouts: the §28 owner cases, front = rotation r: 2 connectors -> splitter -> 2 belts (owner 16.png), connector ->
// connector, connector -> building, splitter -> splitter, everything unconnected. box(kind, looks) draws one box; old
// = the pre-§28 sprites (no edge quads).
func (a *jassets) portLayouts(box func(bi int, looks []int) *image.NRGBA) []*image.NRGBA {
	const C, S, B, X, N = lookCap, lookSeal, lookBelt, lookCoupling, -1
	belt := a.get("belt_anim/straight_00.png")
	room := a.room()
	return []*image.NRGBA{
		a.scene(4, 2, []scenePart{{belt, 0, 0, 3}, {belt, 0, 1, 3}, {box(1, []int{B, B, X, X, N, N}), 1, 0, 3},
			{box(0, []int{X, C, S, C}), 2, 0, 3}, {box(0, []int{X, C, S, C}), 2, 1, 3}, {room, 3, 0, 0}, {room, 3, 1, 0}}),
		a.scene(4, 1, []scenePart{{room, 0, 0, 0}, {box(0, []int{X, C, S, C}), 1, 0, 1}, {box(0, []int{B, C, X, C}), 2, 0, 1}, {belt, 3, 0, 1}}),
		a.scene(3, 1, []scenePart{{belt, 0, 0, 1}, {box(0, []int{S, C, B, C}), 1, 0, 1}, {room, 2, 0, 0}}),
		a.scene(2, 4, []scenePart{{belt, 0, 0, 0}, {belt, 1, 0, 0}, {box(1, []int{B, B, X, X, N, N}), 0, 1, 0}, {box(1, []int{X, X, B, B, N, N}), 0, 2, 0}, {belt, 0, 3, 0}, {belt, 1, 3, 0}}),
		a.scene(3, 3, []scenePart{{box(0, []int{C, C, C, C}), 0, 0, 0}, {box(1, []int{C, C, C, C, N, N}), 0, 2, 0}}),
	}
}

// sheet lays rows of images out at scale s on dark grey with gap g; frame colours (nil = none) are drawn around each.
func sheet(rows [][]*image.NRGBA, frames [][]color.NRGBA, s, g int) *image.NRGBA {
	w, h := g, g
	for _, row := range rows {
		rw, rh := g, 0
		for _, im := range row {
			rw += im.Rect.Dx()*s + g
			rh = max(rh, im.Rect.Dy()*s)
		}
		w, h = max(w, rw), h+rh+g
	}
	out := image.NewNRGBA(image.Rect(0, 0, w, h))
	fill(out, color.NRGBA{40, 40, 40, 255})
	y := g
	for ri, row := range rows {
		x, rh := g, 0
		for ci, im := range row {
			iw, ih := im.Rect.Dx()*s, im.Rect.Dy()*s
			if frames != nil {
				f := image.NewNRGBA(image.Rect(0, 0, iw+8, ih+8))
				fill(f, frames[ri][ci])
				paste(out, f, x-4, y-4)
			}
			paste(out, resize(im, iw, ih), x, y)
			x += iw + g
			rh = max(rh, ih)
		}
		y += rh + g
	}
	return out
}

// portsPreview: row 1 before (pre-§28 box sprites from beforeDir), row 2 after (rules floor + edge quads), at 2x.
func portsPreview(a *jassets, beforeDir, path string) {
	before := func(bi int, _ []int) *image.NRGBA {
		return load(filepath.Join(beforeDir, "belt_"+boxes[bi].name+".png"))
	}
	after := func(bi int, looks []int) *image.NRGBA { return a.box(bi, false, looks, 0).img }
	save(sheet([][]*image.NRGBA{a.portLayouts(before), a.portLayouts(after)}, nil, 2, 32), path)
}

// jointsPreview writes one contact sheet per receiver group (rows = sources, columns = receivers; world up = flow up,
// frame 0, no flips) framed green (mates in every transform) or red, plus a legend with every failing pair.
func jointsPreview(a *jassets, path string) {
	res := joints(a, animFrames)
	ok := map[string]bool{}
	var legend strings.Builder
	for _, r := range res {
		ok[r.src+r.dst] = len(r.fails) == 0
		if len(r.fails) > 0 {
			fmt.Fprintf(&legend, "FAIL %s -> %s: %s\n", r.src, r.dst, strings.Join(r.fails, "; "))
		}
	}
	groups := []struct{ name, match string }{{"rear", ">straight >join_l >join_r >join_lr"}, {"side", "(L) (R)"}, {"crossing", "crossing"}, {"boxes", "connector splitter"}}
	srcs, dsts := a.sources(), a.receivers()
	base := strings.TrimSuffix(path, filepath.Ext(path))
	for _, g := range groups {
		var cols []jgen
		for _, d := range dsts {
			for m := range strings.FieldsSeq(g.match) {
				if (m[0] == '>' && d.name == m) || (m[0] != '>' && strings.Contains(d.name, m)) {
					cols = append(cols, d)
					break
				}
			}
		}
		fmt.Fprintf(&legend, "\n%s_%s.png columns: %s\nrows:", base, g.name, names(cols))
		var rows [][]*image.NRGBA
		var frames [][]color.NRGBA
		for _, s := range srcs {
			var row []*image.NRGBA
			var fr []color.NRGBA
			for _, d := range cols {
				src, dst := s.make(false, 0, 0, isBox(d.name)), d.make(false, 0, 0, isBox(s.name))
				rs, sc := place(src, 0)
				rd, dc := place(dst, 2)
				parts := []scenePart{{src.img, 1 - sc.X, 2 - sc.Y, rs}, {dst.img, 1 - dc.X, 1 - dc.Y, rd}} // both ports in column 1
				row = append(row, a.scene(3, 3, parts))
				fr = append(fr, map[bool]color.NRGBA{true: {40, 200, 60, 255}, false: {230, 40, 40, 255}}[ok[s.name+d.name]])
			}
			rows, frames = append(rows, row), append(frames, fr)
		}
		fmt.Fprintf(&legend, " %s\n", names(srcs))
		save(sheet(rows, frames, 2, 24), base+"_"+g.name+".png")
	}
	if err := os.WriteFile(base+"_legend.txt", []byte(legend.String()), 0o644); err != nil {
		fail(err)
	}
}

func names(g []jgen) string {
	var n []string
	for _, x := range g {
		n = append(n, x.name)
	}
	return strings.Join(n, ", ")
}
