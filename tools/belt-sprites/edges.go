package main

import (
	"fmt"
	"image"
	"os"
	"path/filepath"
)

// §28 connection-aware port edges (docs/BELTS_DESIGN.md): every port edge of a connector / splitter gets one of four
// looks chosen by the C# renderer from what is next to it (belts/src/BeltRenderState.cs EdgeLook, same order).
var edgeLooks = []string{"cap", "seal", "belt", "coupling"}

const (
	lookCap = iota
	lookSeal
	lookBelt
	lookCoupling
)

// box: part prefix, width in tiles, and per edge (BeltRenderState edge order) the port zone in the 64 px part frame and
// its outward direction. Zones span the belt rail extent x 8..56 of a tile (belt_anim straight row 0), cell-centred, and
// reach from the cell edge to the housing frame (measured on the baked level-0 sprites), so a stub or coupling half
// continues the belt / its partner exactly at the shared edge.
// Splitter edges 4 / 5 are its short sides (sim side entry, §7a): only Belt / Coupling when linked, else its housing wall.
type box struct {
	name  string
	w     int
	zones []image.Rectangle
	out   []int
}

var boxes = []box{
	{"connector", 1, []image.Rectangle{image.Rect(8, 0, 56, 9), image.Rect(52, 8, 64, 56), image.Rect(8, 50, 56, 64), image.Rect(0, 8, 12, 56)}, []int{0, 1, 2, 3}},
	{"splitter", 2, []image.Rectangle{image.Rect(8, 0, 56, 11), image.Rect(72, 0, 120, 11), image.Rect(8, 45, 56, 64), image.Rect(72, 45, 120, 64), image.Rect(0, 8, 8, 56), image.Rect(120, 8, 128, 56)}, []int{0, 0, 2, 2, 3, 1}},
}

// edgeHasLook: port edges (0..3) take every look, splitter side walls only the linked ones.
func edgeHasLook(e, l int) bool { return e < 4 || l == lookBelt || l == lookCoupling }

// beltRot: quarter turns of the belt flowing through a zone facing out (into the box at intakes, out at the front).
var beltRot = [4]int{0, 3, 0, 1}

// edgeMasters: Codex image_gen pieces drawn for the Up edge (top row = the cell edge), stored at 4 px per game px.
var edgeMasters = []struct {
	name string
	w, h int
}{{"belt_edge_cap", 192, 48}, {"belt_edge_seal", 192, 48}, {"belt_edge_coupling", 192, 96}}

func rot(im *image.NRGBA, r int) *image.NRGBA {
	for ; r > 0; r-- {
		im = rotateCW(im)
	}
	return im
}

// couplingHalf: the half of the coupling master on one side of its seam (bottom half, seam on top), made left-right
// symmetric from its left half so the two halves of any pair (rotated, mirrored) meet pixel for pixel at the edge.
func couplingHalf(m *image.NRGBA) *image.NRGBA {
	w, h := m.Rect.Dx(), m.Rect.Dy()
	out := image.NewNRGBA(image.Rect(0, 0, w, h/2))
	for y := range h / 2 {
		for x := range w {
			sx := min(x, w-1-x)
			out.SetNRGBA(x, y, m.NRGBAAt(sx, h/2+y))
		}
	}
	return out
}

// edgePiece: look l of zone e of b at damage level lvl as a full part-sized transparent sprite with only the zone set.
// belts[lvl] = the baked static belt sprite of that level (stubs are its crop: rails align by construction).
func edgePiece(b box, e, l, lvl int, pieces [][]*image.NRGBA, belts, decals []*image.NRGBA) *image.NRGBA {
	z := b.zones[e]
	out := image.NewNRGBA(image.Rect(0, 0, b.w*64, 64))
	if l == lookBelt {
		src := rot(belts[lvl], beltRot[b.out[e]])
		for y := z.Min.Y; y < z.Max.Y; y++ {
			for x := z.Min.X; x < z.Max.X; x++ {
				out.SetNRGBA(x, y, src.NRGBAAt(x%64, y))
			}
		}
		return out
	}
	d := z.Dy()
	if b.out[e]%2 == 1 {
		d = z.Dx()
	}
	p := rot(resize(pieces[l][0], 48, d), b.out[e])
	paste(out, p, z.Min.X, z.Min.Y)
	if lvl > 0 {
		decalOver(out, decals[lvl])
	}
	return out
}

// decalOver blends the §26 damage decal (64 px tile, mirrored on odd tiles, as damageFrame) over the opaque pixels,
// faded out over the 3 px next to each tile edge so the shared edge stays that of the undamaged piece.
func decalOver(m, decal *image.NRGBA) {
	for ty := 0; ty < m.Rect.Dy(); ty += 64 {
		for tx := 0; tx < m.Rect.Dx(); tx += 64 {
			d := decal
			if (tx/64+ty/64)%2 == 1 {
				d = flipH(d)
			}
			for y := range 64 {
				for x := range 64 {
					o := m.NRGBAAt(tx+x, ty+y)
					if o.A == 0 {
						continue
					}
					s := d.NRGBAAt(x, y)
					al := float64(s.A) / 255 * min(float64(min(x, y, 63-x, 63-y))/3, 1)
					mix := func(dc, sc uint8) uint8 { return uint8(float64(dc)*(1-al) + float64(sc)*al + .5) }
					o.R, o.G, o.B = mix(o.R, s.R), mix(o.G, s.G), mix(o.B, s.B)
					m.SetNRGBA(tx+x, ty+y, o)
				}
			}
		}
	}
}

func edgeFile(b box, e, l, lvl int) string {
	return fmt.Sprintf("%s_%d_%s%s.png", b.name, e, edgeLooks[l], damageSuffix[lvl])
}

// loadEdgePieces: per look the Up-oriented piece (nil for belt: cropped from the belt sprite per level).
func loadEdgePieces(artDir string) [][]*image.NRGBA {
	pieces := make([][]*image.NRGBA, len(edgeLooks))
	pieces[lookCap] = []*image.NRGBA{load(filepath.Join(artDir, "belt_edge_cap.png"))}
	pieces[lookSeal] = []*image.NRGBA{load(filepath.Join(artDir, "belt_edge_seal.png"))}
	pieces[lookCoupling] = []*image.NRGBA{couplingHalf(load(filepath.Join(artDir, "belt_edge_coupling.png")))}
	return pieces
}

// bakeEdges writes <out>/belt_edge/<box>_<edge>_<look><damage>.png for every box, edge, look and §26 damage level,
// and returns them [box][edge][look][level] for the rules-floor fallback and previews.
func bakeEdges(artDir, outDir string, decals []*image.NRGBA) [][][][]*image.NRGBA {
	pieces := loadEdgePieces(artDir)
	var belts []*image.NRGBA
	for _, sfx := range damageSuffix {
		belts = append(belts, load(filepath.Join(outDir, "belt"+sfx+".png")))
	}
	dir := filepath.Join(outDir, "belt_edge")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		fail(err)
	}
	all := make([][][][]*image.NRGBA, len(boxes))
	for bi, b := range boxes {
		all[bi] = make([][][]*image.NRGBA, len(b.zones))
		for e := range b.zones {
			all[bi][e] = make([][]*image.NRGBA, len(edgeLooks))
			for l := range edgeLooks {
				for lvl := 0; lvl < len(damageSuffix) && edgeHasLook(e, l); lvl++ {
					im := edgePiece(b, e, l, lvl, pieces, belts, decals)
					all[bi][e][l] = append(all[bi][e][l], im)
					save(im, filepath.Join(dir, edgeFile(b, e, l, lvl)))
				}
			}
		}
	}
	return all
}

// withStubs: the rules floor (no-DLL fallback) = the box with a belt stub on every port, i.e. what a fully belted box
// looks like; the C# edge quads cover exactly these zones with the look the neighbours call for.
func withStubs(m *image.NRGBA, lvl int, edges [][][]*image.NRGBA) *image.NRGBA {
	for e := range 4 {
		paste(m, edges[e][lookBelt][lvl], 0, 0)
	}
	return m
}

func boxOf(master string) int {
	for i, b := range boxes {
		if "belt_"+b.name == master {
			return i
		}
	}
	return -1
}
