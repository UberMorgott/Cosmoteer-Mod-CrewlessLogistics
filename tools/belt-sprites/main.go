// Command belt-sprites bakes the belt/splitter/connector sprites (docs/BELTS_DESIGN.md §11) from the Codex-generated
// masters in art/ by mechanical post-processing only: trim the magenta key bands, area-downscale, compose. No pixel is drawn.
//
//	cd tools\belt-sprites
//	go run . -import <dir with raw Codex *_master.png>   // one-time: key out bands, shrink to 256 px/tile into art/
//	go run .                                             // bake art/ -> ..\..\mod\assets
//	go run . -preview out.png                            // also compose new sprites next to vanilla ones
//	go run . -overhaul out.png                           // §21 cases in game layering + animated out.gif
//	go run . -blueprints out.png                         // blueprint ghosts in 4 rotations over floor (vanilla arrow)
//	go run . -damage out.png                             // §25 crossing arts + §26 damage levels of every part
//	go run . -joints base.png                            // §28 joint matrix contact sheets + legend
//	go run . -ports out.png -before <dir>                // §28 port joints before / after
package main

import (
	"flag"
	"fmt"
	"image"
	"image/color"
	"image/color/palette"
	"image/draw"
	"image/gif"
	"image/png"
	"math"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strings"
)

// masterTile is the stored master resolution per tile; the game uses 64 px per tile.
const masterTile = 256

// sprite: master name, size in tiles (w, h), output names for the 64 px/tile sprite, its editor icon (32 px/tile) and
// its blueprint (64 px/tile, derived from the same master by blueprintOf; "" = vanilla blueprint inherited).
type sprite struct {
	master                string
	w, h                  int
	file, icon, blueprint string
}

var sprites = []sprite{
	{"belt", 1, 1, "belt.png", "belt_icon.png", ""},
	{"belt_connector", 1, 1, "belt_connector.png", "belt_connector_icon.png", "belt_connector_blueprint.png"},
	{"belt_splitter", 2, 1, "belt_splitter.png", "belt_splitter_icon.png", "belt_splitter_blueprint.png"},
	// §25 belt crossing, baked rotated 90 degrees clockwise into the part frame (belt flows right, network on Up/Down):
	// rules floor = the plain belt (no-DLL fallback; the C# surface shows the linked network), icon and blueprint = the
	// mixed master (heat pipe and cable both, see mixedCrossing).
	{"belt", 1, 1, "belt_crossing.png", "", ""},
	{"belt_crossing", 1, 1, "", "belt_crossing_icon.png", "belt_crossing_blueprint.png"},
}

// mixedCrossing: crossing masters are drawn like belt.png (belt up, heat pipe / cable entering at the left and right
// edges under the belt). The mixed master keeps base's belt, rails and left entry and takes the right entry (columns from
// the belt band's right edge) from other: no new pixel, the belt is never cut.
func mixedCrossing(base, other *image.NRGBA) *image.NRGBA {
	out := image.NewNRGBA(base.Rect)
	copy(out.Pix, base.Pix)
	for y := range base.Rect.Dy() {
		for x := surf1; x < base.Rect.Dx(); x++ {
			out.SetNRGBA(x, y, other.NRGBAAt(x, y))
		}
	}
	return out
}

// §26 damage stages, like vanilla floors (corridor floor.png / floor_33.png / floor_66.png): level k > 0 overlays the
// Codex damage decal art/belt_damage_<suffix>.png on the part pixels and puts the vanilla damaged corridor floor in the
// margins. Same alignment by construction: the part art itself is untouched under the decal.
var damageSuffix = []string{"", "_33", "_66"}

// keyMagenta turns a Codex decal on the flat magenta key into alpha: grey/brown marks blended with magenta (255, 0, 255)
// keep their own colour (un-mixed), alpha from how far R/B drop toward G.
func keyMagenta(img *image.NRGBA) *image.NRGBA {
	out := image.NewNRGBA(img.Rect)
	for y := img.Rect.Min.Y; y < img.Rect.Max.Y; y++ {
		for x := img.Rect.Min.X; x < img.Rect.Max.X; x++ {
			c := img.NRGBAAt(x, y)
			a := 1 - (float64(min(c.R, c.B))-float64(c.G))/255
			a = min(max((a-0.08)/0.92, 0), 1) // key noise around pure magenta -> 0
			if a == 0 {
				continue
			}
			un := func(v uint8, m float64) uint8 { return uint8(min(max((float64(v)-(1-a)*m)/a, 0), 255)) }
			out.SetNRGBA(x, y, color.NRGBA{un(c.R, 255), un(c.G, 0), un(c.B, 255), uint8(a * 255)})
		}
	}
	return out
}

// damageFrame: frame m (64 px/tile, before floorMargins) at level k: floor margins from the level's vanilla corridor floor,
// decal (64 px tile, mirrored on odd tiles) only over part pixels, i.e. pixels the level-0 bake keeps (keep = floorKeep).
// Animation frames pass the moving surface (mask alpha >= 128) and its scrolled decal surf (surfaceDecal); nil = static.
func damageFrame(m *image.NRGBA, floors []*image.NRGBA, decals []*image.NRGBA, k int, post func(*image.NRGBA, *image.NRGBA), surf, mask *image.NRGBA, keep ...image.Rectangle) *image.NRGBA {
	base := floorMargins(m, floors[0], keep...)
	if post != nil {
		post(base, floors[0])
	}
	if k == 0 {
		return base
	}
	out := floorMargins(m, floors[k], keep...)
	if post != nil {
		post(out, floors[k])
	}
	for ty := 0; ty < m.Rect.Dy(); ty += 64 {
		for tx := 0; tx < m.Rect.Dx(); tx += 64 {
			d := decals[k]
			if (tx/64+ty/64)%2 == 1 {
				d = flipH(d)
			}
			for y := range 64 {
				for x := range 64 {
					fp := floors[0].NRGBAAt((tx+x)%floors[0].Rect.Dx(), (ty+y)%floors[0].Rect.Dy())
					if base.NRGBAAt(tx+x, ty+y) == fp {
						continue // floor pixel: the vanilla damaged floor already shows the damage
					}
					s, o := d.NRGBAAt(x, y), out.NRGBAAt(tx+x, ty+y)
					if mask != nil && mask.NRGBAAt(tx+x, ty+y).A >= 128 {
						s = surf.NRGBAAt(tx+x, ty+y) // moving surface: its decal scrolls with it, rails / frame keep the static one
					}
					// Belt ends and rails join the neighbour tile: fade the decal out over the 3 px next to each tile edge (Codex).
					edge := min(x, y, 63-x, 63-y)
					al := float64(s.A) / 255 * min(float64(edge)/3, 1)
					mix := func(dc, sc uint8) uint8 { return uint8(float64(dc)*(1-al) + float64(sc)*al + .5) }
					out.SetNRGBA(tx+x, ty+y, color.NRGBA{mix(o.R, s.R), mix(o.G, s.G), mix(o.B, s.B), o.A})
				}
			}
		}
	}
	return out
}

// loadDamage: the vanilla corridor floors and the 64 px decals per level (index 0 unused).
func loadDamage(game, artDir string) (floors, decals []*image.NRGBA) {
	for k, sfx := range damageSuffix {
		floors = append(floors, load(filepath.Join(game, "Data", "ships", "terran", "corridor", "floor"+sfx+".png")))
		if k == 0 {
			decals = append(decals, nil)
			continue
		}
		decals = append(decals, resize(load(filepath.Join(artDir, "belt_damage"+sfx+".png")), 64, 64))
	}
	return floors, decals
}

// master loads a master by name; belt_crossing is the mixed heat (left = part Up) / cable (right = part Down) master.
func master(artDir, name string) *image.NRGBA {
	if name == "belt_crossing" {
		return mixedCrossing(load(filepath.Join(artDir, "belt_heat_crossing.png")), load(filepath.Join(artDir, "belt_cable_crossing.png")))
	}
	return load(filepath.Join(artDir, name+".png"))
}

// blueprintRamp: colour by luminance rank (0 = darkest), measured on the former Codex splitter blueprint master
// (quantiles 0/.1/.25/.5/.75/1), so derived blueprints keep the vanilla-like translucent blue look.
var blueprintRamp = []struct {
	t       float64
	r, g, b float64
}{{0, 0, 107, 237}, {.1, 16, 140, 247}, {.25, 49, 177, 253}, {.5, 104, 202, 252}, {.75, 160, 222, 252}, {1, 166, 225, 252}}

// blueprintOf maps the part art to the blueprint ramp by luminance rank (histogram match, no pixel drawn) and keys alpha
// from the red channel like vanilla blueprints (pale ~A76, lines ~A165+).
func blueprintOf(m *image.NRGBA) *image.NRGBA {
	out := image.NewNRGBA(m.Rect)
	n := len(m.Pix) / 4
	lum := make([]float64, n)
	idx := make([]int, n)
	for i := range n {
		p := m.Pix[4*i:]
		lum[i] = 0.299*float64(p[0]) + 0.587*float64(p[1]) + 0.114*float64(p[2])
		idx[i] = i
	}
	sort.Slice(idx, func(a, b int) bool { return lum[idx[a]] < lum[idx[b]] })
	for rank, i := range idx {
		t := float64(rank) / float64(max(n-1, 1))
		k := 1
		for k < len(blueprintRamp)-1 && blueprintRamp[k].t < t {
			k++
		}
		lo, hi := blueprintRamp[k-1], blueprintRamp[k]
		f := (t - lo.t) / (hi.t - lo.t)
		r := lo.r + f*(hi.r-lo.r)
		o := out.Pix[4*i:]
		o[0], o[1], o[2] = uint8(r), uint8(lo.g+f*(hi.g-lo.g)), uint8(lo.b+f*(hi.b-lo.b))
		o[3] = uint8((255 - r) * 0.8)
	}
	return out
}

// vanillaArrow lifts the direction arrow out of the vanilla conveyor blueprint (64 px, front = up): per row, every pixel
// from the first to the last opaque outline pixel (outline + its enclosed fill), made fully opaque so it reads over our
// busy blueprint art. Returns a 64 px tile holding only the arrow at its vanilla position.
func vanillaArrow(game string) *image.NRGBA {
	bp := load(filepath.Join(game, "Data", "ships", "terran", "conveyor", "blueprints.png"))
	out := image.NewNRGBA(bp.Rect)
	for y := range bp.Rect.Dy() {
		x0, x1 := -1, -1
		for x := range bp.Rect.Dx() {
			if bp.NRGBAAt(x, y).A > 200 {
				if x0 < 0 {
					x0 = x
				}
				x1 = x
			}
		}
		for x := x0; x >= 0 && x <= x1; x++ {
			c := bp.NRGBAAt(x, y)
			c.A = 255
			out.SetNRGBA(x, y, c)
		}
	}
	return out
}

// markBlueprint pastes the vanilla arrow onto each tile of a blueprint (rot = clockwise quarter turns of the flow).
func markBlueprint(bp, arrow *image.NRGBA, rot int) *image.NRGBA {
	for ; rot > 0; rot-- {
		arrow = rotateCW(arrow)
	}
	for ty := 0; ty < bp.Rect.Dy(); ty += 64 {
		for tx := 0; tx < bp.Rect.Dx(); tx += 64 {
			paste(bp, arrow, tx, ty)
		}
	}
	return bp
}

// blueprints lays out every belt-part ghost (plain belt = vanilla conveyor blueprint) in all 4 rotations over a vanilla
// corridor floor tile, row 1 at game scale (64 px/tile), row 2 at 2x.
func blueprints(game, assets, path string) {
	floor := load(filepath.Join(game, "Data", "ships", "terran", "corridor", "floor.png"))
	ghosts := []*image.NRGBA{load(filepath.Join(game, "Data", "ships", "terran", "conveyor", "blueprints.png"))}
	for _, sp := range sprites {
		if sp.blueprint != "" {
			ghosts = append(ghosts, load(filepath.Join(assets, sp.blueprint)))
		}
	}
	const gap = 16
	rowW := gap
	for _, g := range ghosts {
		rowW += 4*(max(g.Rect.Dx(), g.Rect.Dy())+gap) + gap
	}
	out := image.NewNRGBA(image.Rect(0, 0, 2*rowW, gap+128+gap+256+gap))
	for i := range out.Pix {
		out.Pix[i] = 40
		if i%4 == 3 {
			out.Pix[i] = 255
		}
	}
	for row, scale := range []int{1, 2} {
		x, y := gap, gap+row*(128+gap)
		for _, g := range ghosts {
			im := g
			for range 4 {
				w, h := im.Rect.Dx()*scale, im.Rect.Dy()*scale
				for ty := 0; ty < h; ty += 64 * scale {
					for tx := 0; tx < w; tx += 64 * scale {
						paste(out, resize(floor, 64*scale, 64*scale), x+tx, y+ty)
					}
				}
				paste(out, resize(im, w, h), x, y)
				x += max(g.Rect.Dx(), g.Rect.Dy())*scale + gap
				im = rotateCW(im)
			}
			x += gap
		}
	}
	save(out, path)
}

// layers are masters baked by code below, not by the sprites loop: belt_lamp = Codex indicator glow on black.
var layers = []sprite{{master: "belt_lamp", w: 1, h: 1}}

// Lamp lens centres on the Codex housing masters (fraction of the part sprite, front = up), measured on the masters;
// splitter lamp i sits above output i (part cell (i, 0)). Glow diameter in tiles.
var lamps = []struct {
	file   string
	w, h   int
	cx, cy float64
}{
	{"belt_connector_lamp.png", 1, 1, 0.5, 0.298},
	{"belt_splitter_lamp_0.png", 2, 1, 0.270, 0.258},
	{"belt_splitter_lamp_1.png", 2, 1, 0.729, 0.258},
}

const lampSize = 0.25

// bakeLamps keys the black glow master to alpha (alpha = brightest channel, for the additive lights_add layer) and
// pastes it, scaled, at each lamp centre of a transparent part-sized overlay.
func bakeLamps(artDir, outDir string) {
	g := load(filepath.Join(artDir, "belt_lamp.png"))
	for i := 0; i < len(g.Pix); i += 4 {
		g.Pix[i+3] = max(g.Pix[i], g.Pix[i+1], g.Pix[i+2])
	}
	d := int(lampSize * 64)
	glow := resize(g, d, d)
	for _, l := range lamps {
		out := image.NewNRGBA(image.Rect(0, 0, l.w*64, l.h*64))
		paste(out, glow, int(l.cx*float64(l.w*64))-d/2, int(l.cy*float64(l.h*64))-d/2)
		save(out, filepath.Join(outDir, l.file))
	}
}

func exists(path string) bool { _, err := os.Stat(path); return err == nil }

func load(path string) *image.NRGBA {
	f, err := openFile(path, false)
	if err != nil {
		fail(err)
	}
	defer func() { _ = f.Close() }() // read-only: nothing to lose
	src, err := png.Decode(f)
	if err != nil {
		fail(fmt.Errorf("%s: %w", path, err))
	}
	b := src.Bounds()
	dst := image.NewNRGBA(image.Rect(0, 0, b.Dx(), b.Dy()))
	for y := 0; y < b.Dy(); y++ {
		for x := 0; x < b.Dx(); x++ {
			dst.Set(x, y, src.At(b.Min.X+x, b.Min.Y+y))
		}
	}
	return dst
}

func save(img *image.NRGBA, path string) {
	f, err := openFile(path, true)
	if err != nil {
		fail(err)
	}
	if err := png.Encode(f, img); err != nil {
		fail(err)
	}
	if err := f.Close(); err != nil {
		fail(err)
	}
	fmt.Println("wrote", path, img.Rect.Dx(), "x", img.Rect.Dy())
}

// openFile opens (or creates) path scoped to its own directory, so the name cannot escape it.
func openFile(path string, create bool) (*os.File, error) {
	root, err := os.OpenRoot(filepath.Dir(path))
	if err != nil {
		return nil, err
	}
	defer func() { _ = root.Close() }() // a directory handle only; files opened in it stay open
	if create {
		return root.Create(filepath.Base(path))
	}
	return root.Open(filepath.Base(path))
}

// crop returns the sub-image of img inside r (SubImage of an *image.NRGBA is always one).
func crop(img *image.NRGBA, r image.Rectangle) *image.NRGBA {
	sub, ok := img.SubImage(r).(*image.NRGBA)
	if !ok {
		fail(fmt.Errorf("crop: unexpected %T", img.SubImage(r)))
	}
	return sub
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, err)
	os.Exit(1)
}

func magenta(c color.NRGBA) bool { return c.R > 200 && c.G < 80 && c.B > 200 }

// trimKeyRows drops top/bottom rows that are mostly the magenta key colour (Codex pads 2:1 parts to its 3:2 canvas).
func trimKeyRows(img *image.NRGBA) *image.NRGBA {
	w, h := img.Rect.Dx(), img.Rect.Dy()
	keyRow := func(y int) bool {
		n := 0
		for x := range w {
			if magenta(img.NRGBAAt(x, y)) {
				n++
			}
		}
		return n*2 > w
	}
	y0, y1 := 0, h
	for y0 < h && keyRow(y0) {
		y0++
	}
	for y1 > y0 && keyRow(y1-1) {
		y1--
	}
	return crop(img, image.Rect(0, y0, w, y1))
}

// resize is an alpha-weighted area average (exact for integer downscales, nearest-area otherwise).
func resize(img *image.NRGBA, w, h int) *image.NRGBA {
	out := image.NewNRGBA(image.Rect(0, 0, w, h))
	ox, oy := img.Rect.Min.X, img.Rect.Min.Y
	sw, sh := img.Rect.Dx(), img.Rect.Dy()
	for y := range h {
		y0, y1 := y*sh/h, max((y+1)*sh/h, y*sh/h+1)
		for x := range w {
			x0, x1 := x*sw/w, max((x+1)*sw/w, x*sw/w+1)
			var r, g, b, a, n float64
			for sy := y0; sy < y1; sy++ {
				for sx := x0; sx < x1; sx++ {
					c := img.NRGBAAt(ox+sx, oy+sy)
					al := float64(c.A)
					r += float64(c.R) * al
					g += float64(c.G) * al
					b += float64(c.B) * al
					a += al
					n++
				}
			}
			if a > 0 {
				out.SetNRGBA(x, y, color.NRGBA{uint8(r / a), uint8(g / a), uint8(b / a), uint8(a / n)})
			}
		}
	}
	return out
}

// rotateCW turns an image 90 degrees clockwise (top row -> right column; w x h -> h x w).
func rotateCW(img *image.NRGBA) *image.NRGBA {
	n := img.Rect.Dy()
	out := image.NewNRGBA(image.Rect(0, 0, n, img.Rect.Dx()))
	for y := range img.Rect.Dx() {
		for x := range n {
			out.SetNRGBA(x, y, img.NRGBAAt(img.Rect.Min.X+y, img.Rect.Min.Y+n-1-x))
		}
	}
	return out
}

// paste copies src onto dst at (ox, oy) with alpha blending.
func paste(dst, src *image.NRGBA, ox, oy int) {
	for y := 0; y < src.Rect.Dy(); y++ {
		for x := 0; x < src.Rect.Dx(); x++ {
			s := src.NRGBAAt(src.Rect.Min.X+x, src.Rect.Min.Y+y)
			a := float64(s.A) / 255
			d := dst.NRGBAAt(ox+x, oy+y)
			mix := func(dc, sc uint8) uint8 { return uint8(float64(dc)*(1-a) + float64(sc)*a) }
			dst.SetNRGBA(ox+x, oy+y, color.NRGBA{mix(d.R, s.R), mix(d.G, s.G), mix(d.B, s.B), uint8((a + float64(d.A)/255*(1-a)) * 255)})
		}
	}
}

// preview lays out, at 2x game scale on dark grey: vanilla corridor + conveyor, then every new sprite, then an in-game
// style row connector -> belt -> belt -> connector (rotated parts are skipped: preview is read top = front).
func preview(game, assets, path string) {
	const s = 128
	vanilla := filepath.Join(game, "Data", "ships", "terran")
	row1 := []*image.NRGBA{load(filepath.Join(vanilla, "corridor", "floor.png")), load(filepath.Join(vanilla, "conveyor", "conveyor1.png"))}
	for _, sp := range sprites {
		if sp.file != "" {
			row1 = append(row1, load(filepath.Join(assets, sp.file)))
		}
	}
	col := func(name string) *image.NRGBA { return load(filepath.Join(assets, name)) }
	row2 := []*image.NRGBA{load(filepath.Join(vanilla, "corridor", "floor.png")), col("belt_connector.png"), col("belt.png"), col("belt.png"), col("belt_splitter.png"), col("belt.png"), col("belt_connector.png"), load(filepath.Join(vanilla, "corridor", "floor.png"))}
	width := func(row []*image.NRGBA, gap int) int {
		w := gap
		for _, im := range row {
			w += im.Rect.Dx()*s/64 + gap
		}
		return w
	}
	W := max(width(row1, 16), width(row2, 0)+32)
	out := image.NewNRGBA(image.Rect(0, 0, W, 5*s+80))
	for i := range out.Pix {
		out.Pix[i] = 40
		if i%4 == 3 {
			out.Pix[i] = 255
		}
	}
	x := 16
	for _, im := range row1 {
		paste(out, resize(im, im.Rect.Dx()*s/64, s), x, 16)
		x += im.Rect.Dx()*s/64 + 16
	}
	x = 16
	for _, im := range row2 {
		paste(out, resize(im, im.Rect.Dx()*s/64, s), x, s+32)
		x += im.Rect.Dx() * s / 64
	}
	// Flow columns (front = top): belts feeding into and out of the splitter and the connector, to check edge joins.
	put := func(name string, tx, ty int) {
		im := col(name)
		paste(out, resize(im, im.Rect.Dx()*s/64, im.Rect.Dy()*s/64), 16+tx*s, 2*s+48+ty*s)
	}
	for ty := 0; ty < 3; ty += 2 {
		put("belt.png", 0, ty)
		put("belt.png", 1, ty)
		put("belt.png", 3, ty)
	}
	put("belt_splitter.png", 0, 1)
	put("belt_connector.png", 3, 1)
	// Editor icons and the splitter blueprint at their native pixel size, 2x.
	ix := 16 + 5*s
	for _, name := range []string{"belt_splitter_icon.png", "belt_connector_icon.png", "belt_icon.png"} {
		im := col(name)
		paste(out, resize(im, im.Rect.Dx()*2, im.Rect.Dy()*2), ix, 2*s+48)
		ix += im.Rect.Dx()*2 + 16
	}
	bp := col("belt_splitter_blueprint.png")
	paste(out, resize(bp, 2*s, s), 16+5*s, 3*s+48)
	save(out, path)
}

// crossings lays out, at 2x game scale, each §25 crossing art (animation frame 0) in the part frame (rotation 0: belt
// flows right) between two horizontal belts, with what it links to above and below it: vanilla heat pipe / our cable
// straight sprite or nothing (plain).
func crossings(game, assets, path string) {
	const s = 128
	pipe := load(filepath.Join(game, "Data", "ships", "terran", "heat_pipe_adaptive", "floor_straight.png"))
	cable := load(filepath.Join(assets, "cable_body_straight.png"))
	belt := rotateCW(load(filepath.Join(assets, "belt.png")))
	cases := []struct {
		art      string
		up, down *image.NRGBA
	}{{"heat_crossing", pipe, pipe}, {"cable_crossing", cable, cable}, {"plain_crossing", nil, nil}, {"heat_cable_crossing", pipe, cable}, {"cable_heat_crossing", cable, pipe}}
	out := image.NewNRGBA(image.Rect(0, 0, len(cases)*(3*s+16)+16, 3*s+32))
	for i := range out.Pix {
		out.Pix[i] = 40
		if i%4 == 3 {
			out.Pix[i] = 255
		}
	}
	for k, c := range cases {
		ox := 16 + k*(3*s+16)
		put := func(im *image.NRGBA, tx, ty int) {
			if im != nil {
				paste(out, resize(im, s, s), ox+tx*s, 16+ty*s)
			}
		}
		put(c.up, 1, 0)
		put(c.down, 1, 2)
		put(belt, 0, 1)
		put(load(filepath.Join(assets, "belt_anim", c.art+"_00.png")), 1, 1)
		put(belt, 2, 1)
	}
	save(out, path)
}

// Belt animation (docs/BELTS_DESIGN.md §21): every frame and variant is a mechanical remap of the Codex straight master
// art/belt.png, so curve and join edges equal the straight edges pixel for pixel. Front = up, travel bottom -> top.
const (
	animFrames = 16
	// Belt surface band x15..48 of 64 (BeltWorld.SurfaceWidth) at master scale.
	surf0, surf1 = 15 * masterTile / 64, 49 * masterTile / 64
)

// shiftBand scrolls only the belt surface band up by d rows with wrap-around; rails and floor stay put. The texture
// period is one tile, so frame k of every variant lines up with frame k of its neighbours.
func shiftBand(img *image.NRGBA, d int) *image.NRGBA {
	n := img.Rect.Dy()
	out := image.NewNRGBA(image.Rect(0, 0, img.Rect.Dx(), n))
	copy(out.Pix, img.Pix)
	for y := range n {
		copy(out.Pix[y*out.Stride+surf0*4:y*out.Stride+surf1*4], img.Pix[((y+d)%n)*img.Stride+surf0*4:((y+d)%n)*img.Stride+surf1*4])
	}
	return out
}

// curveEdges makes the 64 px curve edges equal the straight frame's edges exactly (the bilinear polar remap is only
// approximate near the corner): front row = straight front row, left column = straight back row (radius = column).
func curveEdges(c, s *image.NRGBA) {
	n := c.Rect.Dx()
	for i := range n {
		c.SetNRGBA(i, 0, s.NRGBAAt(i, 0))
		c.SetNRGBA(0, i, s.NRGBAAt(i, n-1))
	}
}

// sample is a bilinear read with clamped coordinates (pixel centres at +0.5).
func sample(img *image.NRGBA, fx, fy float64) color.NRGBA {
	n := img.Rect.Dx()
	fx, fy = min(max(fx-0.5, 0), float64(n-1)), min(max(fy-0.5, 0), float64(img.Rect.Dy()-1))
	x0, y0 := int(fx), int(fy)
	x1, y1 := min(x0+1, n-1), min(y0+1, img.Rect.Dy()-1)
	tx, ty := fx-float64(x0), fy-float64(y0)
	var c [4]float64
	for _, p := range [4]struct {
		x, y int
		w    float64
	}{{x0, y0, (1 - tx) * (1 - ty)}, {x1, y0, tx * (1 - ty)}, {x0, y1, (1 - tx) * ty}, {x1, y1, tx * ty}} {
		q := img.NRGBAAt(p.x, p.y)
		c[0] += float64(q.R) * p.w
		c[1] += float64(q.G) * p.w
		c[2] += float64(q.B) * p.w
		c[3] += float64(q.A) * p.w
	}
	return color.NRGBA{uint8(c[0] + .5), uint8(c[1] + .5), uint8(c[2] + .5), uint8(c[3] + .5)}
}

// curveLeft polar-remaps a straight frame onto a quarter turn around the front-left corner (feeder enters at the left
// edge, leaves at the front): radius = source column, angle = source row (left edge = bottom row, front edge = top row).
// Radius beyond the tile clamps to the outer column (corridor floor), so the far corner stays plain floor.
func curveLeft(img *image.NRGBA) *image.NRGBA {
	n := img.Rect.Dx()
	out := image.NewNRGBA(image.Rect(0, 0, n, n))
	for y := range n {
		for x := range n {
			px, py := float64(x)+.5, float64(y)+.5
			r := math.Hypot(px, py)
			a := math.Atan2(py, px) / (math.Pi / 2)
			out.SetNRGBA(x, y, sample(img, r, a*float64(n)))
		}
	}
	return out
}

// join pastes the surface of a side feeder over the receiver's rail band at that side (rows of the surface band only),
// taken from the rotated frame (texture period 1 tile, so the strip is the feeder surface continued past its front edge).
func join(dst, frame *image.NRGBA, left bool) {
	src, x0, x1 := rotateCW(frame), 0, surf0
	if !left {
		src, x0, x1 = rotateCW(rotateCW(rotateCW(frame))), surf1, masterTile
	}
	for y := surf0; y < surf1; y++ {
		for x := x0; x < x1; x++ {
			dst.SetNRGBA(x, y, src.NRGBAAt(x, y))
		}
	}
}

// pasteBand copies the belt surface band x surf0..surf1 of frame onto base (crossing masters drawn like belt.png).
func pasteBand(base, frame *image.NRGBA) *image.NRGBA {
	out := image.NewNRGBA(base.Rect)
	copy(out.Pix, base.Pix)
	for y := range masterTile {
		for x := surf0; x < surf1; x++ {
			out.SetNRGBA(x, y, frame.NRGBAAt(x, y))
		}
	}
	return out
}

// animVariants: output prefix -> builder from straight frame k. Order and names are wired in mod/belt.rules
// BeltSurfaceSprites and belts/src/BeltRender.cs (Variant).
type variant struct {
	name  string
	build func(*image.NRGBA) *image.NRGBA
}

func animVariants(artDir string) []variant {
	heat, cable := load(filepath.Join(artDir, "belt_heat_crossing.png")), load(filepath.Join(artDir, "belt_cable_crossing.png"))
	heatCable, cableHeat := mixedCrossing(heat, cable), mixedCrossing(cable, heat)
	clone := func(f *image.NRGBA) *image.NRGBA { o := image.NewNRGBA(f.Rect); copy(o.Pix, f.Pix); return o }
	return []variant{
		{"straight", func(f *image.NRGBA) *image.NRGBA { return f }},
		{"join_l", func(f *image.NRGBA) *image.NRGBA { o := clone(f); join(o, f, true); return o }},
		{"join_r", func(f *image.NRGBA) *image.NRGBA { o := clone(f); join(o, f, false); return o }},
		{"join_lr", func(f *image.NRGBA) *image.NRGBA { o := clone(f); join(o, f, true); join(o, f, false); return o }},
		{"curve", curveLeft},
		// Crossings: baked into the part frame like their static sprites (belt flows right).
		{"heat_crossing", func(f *image.NRGBA) *image.NRGBA { return rotateCW(pasteBand(heat, f)) }},
		{"cable_crossing", func(f *image.NRGBA) *image.NRGBA { return rotateCW(pasteBand(cable, f)) }},
		// §25: nothing linked (plain belt), heat at part Up + cable at Down, and the reverse.
		{"plain_crossing", rotateCW},
		{"heat_cable_crossing", func(f *image.NRGBA) *image.NRGBA { return rotateCW(pasteBand(heatCable, f)) }},
		{"cable_heat_crossing", func(f *image.NRGBA) *image.NRGBA { return rotateCW(pasteBand(cableHeat, f)) }},
	}
}

// fadeRows: §26 surface damage fades in / out over this many master rows (8 px in game) at the tile entry and exit.
const fadeRows = 8 * masterTile / 64

// surfaceDecal: frame k of the §26 surface damage on the straight master: the decal's belt band scrolled exactly like the
// surface texture (shiftBand, wraps within the tile like its own roller module), alpha ramped to 0 at the entry and exit
// rows so no mark pops at the wrap and tile joins stay those of the undamaged belt; outside the band transparent.
func surfaceDecal(dec *image.NRGBA, k int) *image.NRGBA {
	out := shiftBand(dec, k*masterTile/animFrames)
	for y := range masterTile {
		f := min(float64(min(y, masterTile-1-y))/fadeRows, 1)
		for x := range masterTile {
			i := y*out.Stride + x*4 + 3
			if x < surf0 || x >= surf1 {
				out.Pix[i] = 0
			} else {
				out.Pix[i] = uint8(float64(out.Pix[i])*f + .5)
			}
		}
	}
	return out
}

// animFrame: frame k of variant v (64 px) at every §26 damage level. The surface decal and the surface mask go through
// the variant's own remap, so on curves, joins and crossings the damage follows the surface mapping of the frame.
func animFrame(belt *image.NRGBA, surfDecals []*image.NRGBA, v variant, k int, floors, decals []*image.NRGBA) []*image.NRGBA {
	var post func(*image.NRGBA, *image.NRGBA)
	over := v.build
	if v.name == "curve" {
		post = curveFloor
	}
	if strings.HasSuffix(v.name, "_crossing") {
		over = rotateCW // crossings paste the band onto their master: for a band-only image that is the bare rotation
	}
	remap := func(build func(*image.NRGBA) *image.NRGBA, f *image.NRGBA) *image.NRGBA {
		m := resize(build(f), 64, 64)
		if v.name == "curve" {
			curveEdges(m, resize(f, 64, 64))
		}
		return m
	}
	// Moving up the screen = texture rows scroll up: frame k shows the texture k/animFrames of a tile further on.
	m := remap(v.build, shiftBand(belt, k*masterTile/animFrames))
	band := image.NewNRGBA(belt.Rect)
	for y := range masterTile {
		for x := surf0; x < surf1; x++ {
			band.SetNRGBA(x, y, color.NRGBA{255, 255, 255, 255})
		}
	}
	mask := remap(over, band)
	out := make([]*image.NRGBA, len(damageSuffix))
	for lvl := range damageSuffix {
		var surf *image.NRGBA
		if lvl > 0 {
			surf = remap(over, surfaceDecal(surfDecals[lvl], k))
		}
		out[lvl] = damageFrame(m, floors, decals, lvl, post, surf, mask)
	}
	return out
}

// bakeAnim writes <out>/belt_anim/<variant><damage>_<k>.png (64 px) for k < animFrames and every §26 damage level.
func bakeAnim(artDir, outDir string, floors, decals []*image.NRGBA) {
	belt := load(filepath.Join(artDir, "belt.png"))
	surfDecals := []*image.NRGBA{nil}
	for _, sfx := range damageSuffix[1:] {
		surfDecals = append(surfDecals, load(filepath.Join(artDir, "belt_damage"+sfx+".png")))
	}
	dir := filepath.Join(outDir, "belt_anim")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		fail(err)
	}
	for _, v := range animVariants(artDir) {
		for k := range animFrames {
			for lvl, im := range animFrame(belt, surfDecals, v, k, floors, decals) {
				save(im, filepath.Join(dir, fmt.Sprintf("%s%s_%02d.png", v.name, damageSuffix[lvl], k)))
			}
		}
	}
}

// floorMargins replaces the Codex-painted floor plate outside rails and housings (grey, lum 70..140, reached from the
// sprite border; ~110 vs vanilla ~176) with the vanilla corridor floor pixels at the same tile position, so belt parts sit
// on the same floor as the corridors around them. 1 px seams inside the plate go with it.
func floorMargins(m, floor *image.NRGBA, keep ...image.Rectangle) *image.NRGBA {
	w, h := m.Rect.Dx(), m.Rect.Dy()
	plate := func(c color.NRGBA) bool {
		hi, lo := max(c.R, c.G, c.B), min(c.R, c.G, c.B)
		l := (299*int(c.R) + 587*int(c.G) + 114*int(c.B)) / 1000
		return hi-lo < 25 && l >= 55 && l <= 140
	}
	isPlate := make([]bool, w*h)
	for i := range isPlate {
		isPlate[i] = plate(m.NRGBAAt(i%w, i/w)) && !inAny(image.Pt(i%w, i/w), keep)
	}
	// Flood over the plate eroded by 1 px (outside the sprite counts as plate), so 1-2 px gaps in housing outlines do not
	// leak into the housings, then grow the result back by 1 px over plate pixels.
	core := make([]bool, w*h)
	for y := range h {
		for x := range w {
			core[y*w+x] = true
			for dy := -1; dy <= 1; dy++ {
				for dx := -1; dx <= 1; dx++ {
					nx, ny := x+dx, y+dy
					if nx >= 0 && ny >= 0 && nx < w && ny < h && !isPlate[ny*w+nx] {
						core[y*w+x] = false
					}
				}
			}
		}
	}
	reached := make([]bool, w*h)
	var stack []int
	for y := range h {
		for x := range w {
			if (x == 0 || y == 0 || x == w-1 || y == h-1) && core[y*w+x] {
				reached[y*w+x] = true
				stack = append(stack, y*w+x)
			}
		}
	}
	for len(stack) > 0 {
		i := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		x, y := i%w, i/w
		for _, d := range [4][2]int{{1, 0}, {-1, 0}, {0, 1}, {0, -1}} {
			nx, ny := x+d[0], y+d[1]
			if nx >= 0 && ny >= 0 && nx < w && ny < h && !reached[ny*w+nx] && core[ny*w+nx] {
				reached[ny*w+nx] = true
				stack = append(stack, ny*w+nx)
			}
		}
	}
	in := make([]bool, w*h)
	for y := range h {
		for x := range w {
			for dy := -1; dy <= 1 && isPlate[y*w+x]; dy++ {
				for dx := -1; dx <= 1; dx++ {
					nx, ny := x+dx, y+dy
					if nx >= 0 && ny >= 0 && nx < w && ny < h && reached[ny*w+nx] {
						in[y*w+x] = true
					}
				}
			}
		}
	}
	at := func(x, y int) bool { return x >= 0 && y >= 0 && x < w && y < h && in[y*w+x] }
	out := image.NewNRGBA(m.Rect)
	copy(out.Pix, m.Pix)
	fw, fh := floor.Rect.Dx(), floor.Rect.Dy()
	for y := range h {
		for x := range w {
			if at(x, y) || at(x-1, y) && at(x+1, y) || at(x, y-1) && at(x, y+1) {
				out.SetNRGBA(x, y, floor.NRGBAAt(x%fw, y%fh))
			}
		}
	}
	return out
}

// chevronSrc: the splitter master's yellow output chevron (master px, points to the front); chevronAt: where it goes on
// the connector master (housing centre).
var chevronSrc, chevronAt = image.Rect(240, 102, 270, 136), image.Pt(113, 102)

// withChevron copies the splitter chevron onto the connector master, weighted by its warm tint (R-B) so only the
// chevron pixels move and the housing plate under it stays.
func withChevron(conn, split *image.NRGBA) *image.NRGBA {
	out := image.NewNRGBA(conn.Rect)
	copy(out.Pix, conn.Pix)
	for y := chevronSrc.Min.Y; y < chevronSrc.Max.Y; y++ {
		for x := chevronSrc.Min.X; x < chevronSrc.Max.X; x++ {
			s := split.NRGBAAt(x, y)
			a := min(max((float64(s.R)-float64(s.B)-12)/24, 0), 1)
			dx, dy := chevronAt.X+x-chevronSrc.Min.X, chevronAt.Y+y-chevronSrc.Min.Y
			d := out.NRGBAAt(dx, dy)
			mix := func(dc, sc uint8) uint8 { return uint8(float64(dc)*(1-a) + float64(sc)*a + .5) }
			out.SetNRGBA(dx, dy, color.NRGBA{mix(d.R, s.R), mix(d.G, s.G), mix(d.B, s.B), d.A})
		}
	}
	return out
}

func inAny(p image.Point, rs []image.Rectangle) bool {
	return slices.ContainsFunc(rs, p.In)
}

// floorKeep: housing areas painted in the same grey as the floor plate (64 px/tile frame), never floor.
var floorKeep = map[string][]image.Rectangle{
	"belt_splitter": {image.Rect(2, 12, 126, 46), image.Rect(48, 46, 80, 51)},
}

// oldStubs: the Codex masters' own port stubs (64 px frame; splitter ones 4 px off the cell centre) erased to floor
// before the §28 centred stubs go on; connector stubs lie inside its new zones.
var oldStubs = map[string][]image.Rectangle{
	"belt_splitter": {image.Rect(4, 0, 60, 11), image.Rect(68, 0, 124, 11), image.Rect(6, 48, 60, 64), image.Rect(68, 48, 122, 64)},
}

// curveFloor: the polar remap smears the straight margin (columns <8 and >=56) into dark seams the colour flood misses;
// on the curve those columns are radii from the front-left corner, so set them to floor by geometry.
func curveFloor(m, floor *image.NRGBA) {
	for y := range m.Rect.Dy() {
		for x := range m.Rect.Dx() {
			if r := math.Hypot(float64(x)+.5, float64(y)+.5); r < 8 || r >= 56 {
				m.SetNRGBA(x, y, floor.NRGBAAt(x, y))
			}
		}
	}
}

// flipH mirrors an image left-right (curve right = mirrored curve left, as flipU at draw time).
func flipH(img *image.NRGBA) *image.NRGBA {
	w, h := img.Rect.Dx(), img.Rect.Dy()
	out := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := range h {
		for x := range w {
			out.SetNRGBA(x, y, img.NRGBAAt(img.Rect.Min.X+w-1-x, img.Rect.Min.Y+y))
		}
	}
	return out
}

// addLight composites an additive lights_add overlay (rgb * alpha added).
func addLight(dst, src *image.NRGBA, ox, oy int) {
	for y := 0; y < src.Rect.Dy(); y++ {
		for x := 0; x < src.Rect.Dx(); x++ {
			s, d := src.NRGBAAt(x, y), dst.NRGBAAt(ox+x, oy+y)
			a := float64(s.A) / 255
			add := func(dc, sc uint8) uint8 { return uint8(min(255, float64(dc)+float64(sc)*a)) }
			dst.SetNRGBA(ox+x, oy+y, color.NRGBA{add(d.R, s.R), add(d.G, s.G), add(d.B, s.B), d.A})
		}
	}
}

// overhaul composes the §21 cases in game layering (floor < animated surface < lamps) at 64 px/tile for animation
// frame k: straight, curve L/R, sideload T-joint, connector room -> belt side (owner case), connector belt -> room,
// splitter with one output unconnected, heat/cable crossings. Front = up unless rotated; rot = clockwise quarter turns.
func overhaul(game, assets string, k int, lit bool) *image.NRGBA {
	const W, H = 20, 6
	out := image.NewNRGBA(image.Rect(0, 0, W*64, H*64))
	floor := load(filepath.Join(game, "Data", "ships", "terran", "corridor", "floor.png"))
	room := load(filepath.Join(game, "Data", "ships", "terran", "storage_2x2", "floor.png"))
	room = resize(crop(room, image.Rect(0, 0, room.Rect.Dx()/2, room.Rect.Dy()/2)), 64, 64)
	for ty := range H {
		for tx := range W {
			paste(out, resize(floor, 64, 64), tx*64, ty*64)
		}
	}
	rot := func(im *image.NRGBA, r int) *image.NRGBA {
		for ; r > 0; r-- {
			im = rotateCW(im)
		}
		return im
	}
	a := func(v string) *image.NRGBA {
		return load(filepath.Join(assets, "belt_anim", fmt.Sprintf("%s_%02d.png", v, k)))
	}
	put := func(im *image.NRGBA, tx, ty int) { paste(out, im, tx*64, ty*64) }
	belt := func(tx, ty, r int) { put(rot(a("straight"), r), tx, ty) }
	// Straight column; curve left (feeder from the left) and right (mirrored, feeder from the right).
	for y := 1; y <= 4; y++ {
		belt(0, y, 0)
	}
	belt(1, 4, 1)
	put(a("curve"), 2, 4)
	belt(2, 3, 0)
	belt(2, 2, 0)
	belt(4, 4, 3)
	put(flipH(a("curve")), 3, 4)
	belt(3, 3, 0)
	belt(3, 2, 0)
	// Sideload T-joint: main line up, feeder from the left joins at (6,2).
	for y := 1; y <= 4; y++ {
		belt(6, y, 0)
	}
	put(a("join_l"), 6, 2)
	belt(5, 2, 1)
	// Owner left-circle case: room -> connector (pointing left) -> side of a belt with no rear: the belt curves right.
	put(room, 10, 4)
	conn := load(filepath.Join(assets, "belt_connector.png"))
	lamp := load(filepath.Join(assets, "belt_connector_lamp.png"))
	put(rot(conn, 3), 9, 4)
	put(flipH(a("curve")), 8, 4)
	belt(8, 3, 0)
	belt(8, 2, 0)
	// Connector belt -> room.
	belt(12, 4, 0)
	belt(12, 3, 0)
	put(conn, 12, 2)
	put(room, 12, 1)
	// Owner right-circle case: splitter, both inputs fed, only output 0 connected (output 1 port closed).
	put(load(filepath.Join(assets, "belt_splitter.png")), 14, 3)
	belt(14, 4, 0)
	belt(15, 4, 0)
	belt(14, 2, 0)
	belt(14, 1, 0)
	// Crossings in the part frame (belt flows right) between belts.
	for i, c := range []string{"heat_crossing", "cable_crossing"} {
		belt(17, 1+2*i, 1)
		put(a(c), 18, 1+2*i)
		belt(19, 1+2*i, 1)
	}
	if lit {
		addLight(out, rot(lamp, 3), 9*64, 4*64)
		addLight(out, lamp, 12*64, 2*64)
		addLight(out, load(filepath.Join(assets, "belt_splitter_lamp_0.png")), 14*64, 3*64)
	}
	return out
}

// overhaulPreview writes frame 0 (lamps lit) at 2x as PNG and all animation frames as an animated GIF at 1x
// (lamps blink on 2-tick phases like BeltRenderState at full connector rate: lit when (frame & 2) == 0).
func overhaulPreview(game, assets, pngPath string) {
	f0 := overhaul(game, assets, 0, true)
	save(resize(f0, f0.Rect.Dx()*2, f0.Rect.Dy()*2), pngPath)
	var frames []*image.NRGBA
	for k := range animFrames {
		frames = append(frames, overhaul(game, assets, k, k&2 == 0))
	}
	saveGIF(frames, strings.TrimSuffix(pngPath, filepath.Ext(pngPath))+".gif")
}

// saveGIF writes the animation frames (Plan9 palette, dithered) as a looping GIF.
func saveGIF(frames []*image.NRGBA, gifPath string) {
	anim := &gif.GIF{}
	for _, f := range frames {
		p := image.NewPaletted(f.Rect, palette.Plan9)
		draw.FloydSteinberg.Draw(p, f.Rect, f, image.Point{})
		anim.Image = append(anim.Image, p)
		anim.Delay = append(anim.Delay, 3) // 1/32 s per frame is below GIF resolution; 30 ms
	}
	f, err := openFile(gifPath, true)
	if err != nil {
		fail(err)
	}
	if err := gif.EncodeAll(f, anim); err != nil {
		fail(err)
	}
	if err := f.Close(); err != nil {
		fail(err)
	}
	fmt.Println("wrote", gifPath, len(frames), "frames")
}

func main() {
	game := flag.String("game", `D:\Steam\steamapps\common\Cosmoteer`, "game root (vanilla blueprint arrow, previews)")
	artDir := flag.String("art", "art", "master directory")
	outDir := flag.String("out", filepath.Join("..", "..", "mod", "assets"), "output directory")
	importDir := flag.String("import", "", "raw Codex output directory to import into -art first")
	previewPath := flag.String("preview", "", "optional preview PNG path")
	overhaulPath := flag.String("overhaul", "", "optional §21 overhaul preview PNG path (+ animated .gif next to it)")
	crossingsPath := flag.String("crossings", "", "optional crossings preview PNG path (belts left/right, pipe or cable above/below)")
	damagePath := flag.String("damage", "", "optional §25/§26 preview PNG path (crossing arts, damage levels of every part)")
	blueprintsPath := flag.String("blueprints", "", "optional blueprint ghost preview PNG path (4 rotations over floor, 1x and 2x)")
	jointsPath := flag.String("joints", "", "optional §28 joint matrix contact sheets base path (<base>_<group>.png + _legend.txt)")
	portsPath := flag.String("ports", "", "optional §28 port joints before/after preview PNG path (needs -before)")
	beforeDir := flag.String("before", "", "directory with the pre-§28 belt_connector.png / belt_splitter.png for -ports")
	flag.Parse()

	if *importDir != "" {
		// Only masters present in -import are replaced, so one part can be regenerated without touching the others.
		for _, name := range []string{"belt_damage_33", "belt_damage_66"} {
			// §26 Codex damage decals on the magenta key -> alpha, one 256 px tile.
			if path := filepath.Join(*importDir, name+"_master.png"); exists(path) {
				save(resize(keyMagenta(load(path)), masterTile, masterTile), filepath.Join(*artDir, name+".png"))
			}
		}
		for _, em := range edgeMasters {
			// §28 edge pieces on magenta key bands, drawn for the Up edge.
			if path := filepath.Join(*importDir, em.name+"_master.png"); exists(path) {
				save(resize(trimKeyRows(load(path)), em.w, em.h), filepath.Join(*artDir, em.name+".png"))
			}
		}
		for _, sp := range append(sprites, layers...) {
			path := filepath.Join(*importDir, sp.master+"_master.png")
			if !exists(path) {
				continue
			}
			raw := trimKeyRows(load(path))
			fmt.Println("import", sp.master, "rows", raw.Rect.Min.Y, "..", raw.Rect.Max.Y)
			save(resize(raw, sp.w*masterTile, sp.h*masterTile), filepath.Join(*artDir, sp.master+".png"))
		}
	}
	arrow := vanillaArrow(*game)
	floors, decals := loadDamage(*game, *artDir)
	var edges [][][][]*image.NRGBA
	for _, sp := range sprites {
		m := master(*artDir, sp.master)
		bi := boxOf(sp.master)
		if bi >= 0 && edges == nil {
			edges = bakeEdges(*artDir, *outDir, decals) // needs the static belt, baked first (sprites[0])
		}
		// §28 boxes: old stubs and hazard strips erased to floor, centred belt stubs on every port (no-DLL fallback).
		fix := func(im *image.NRGBA, lvl int) *image.NRGBA {
			if bi >= 0 {
				for _, r := range oldStubs[sp.master] {
					fl := floors[lvl]
					for y := r.Min.Y; y < r.Max.Y; y++ {
						for x := r.Min.X; x < r.Max.X; x++ {
							im.SetNRGBA(x, y, fl.NRGBAAt(x%fl.Rect.Dx(), y%fl.Rect.Dy()))
						}
					}
				}
				withStubs(im, lvl, edges[bi])
			}
			return im
		}
		if sp.master == "belt_connector" {
			m = withChevron(m, load(filepath.Join(*artDir, "belt_splitter.png")))
		}
		rot := 0
		if strings.HasSuffix(sp.master, "_crossing") || strings.HasSuffix(sp.file, "_crossing.png") {
			m, rot = rotateCW(m), 1
		}
		for lvl, sfx := range damageSuffix {
			if sp.file != "" {
				out := fix(damageFrame(resize(m, sp.w*64, sp.h*64), floors, decals, lvl, nil, nil, nil, floorKeep[sp.master]...), lvl)
				save(out, filepath.Join(*outDir, strings.TrimSuffix(sp.file, ".png")+sfx+".png"))
			}
		}
		m64 := resize(m, sp.w*64, sp.h*64)
		if bi >= 0 {
			m64 = fix(m64, 0) // icon and blueprint show the corrected port geometry too
		}
		if sp.blueprint != "" {
			save(markBlueprint(blueprintOf(m64), arrow, rot), filepath.Join(*outDir, sp.blueprint))
		}
		if sp.icon != "" {
			icon := resize(m, sp.w*32, sp.h*32)
			if bi >= 0 {
				icon = resize(m64, sp.w*32, sp.h*32)
			}
			save(icon, filepath.Join(*outDir, sp.icon))
		}
	}
	bakeAnim(*artDir, *outDir, floors, decals)
	bakeLamps(*artDir, *outDir)
	if *previewPath != "" {
		preview(*game, *outDir, *previewPath)
	}
	if *overhaulPath != "" {
		overhaulPreview(*game, *outDir, *overhaulPath)
	}
	if *crossingsPath != "" {
		crossings(*game, *outDir, *crossingsPath)
	}
	if *damagePath != "" {
		damagePreview(*game, *outDir, *damagePath)
	}
	if *blueprintsPath != "" {
		blueprints(*game, *outDir, *blueprintsPath)
	}
	if *jointsPath != "" {
		jointsPreview(newJAssets(*game, *outDir), *jointsPath)
	}
	if *portsPath != "" {
		portsPreview(newJAssets(*game, *outDir), *beforeDir, *portsPath)
	}
}

// damagePreview lays out at 2x: row 1 the §25 crossing arts (frame 0: heat, cable, plain, heat+cable, cable+heat) with
// what they link to above and below; then one row per part (belt, curve, connector, splitter, crossings) at the §26 damage
// levels 0 / 33 / 66, over vanilla corridor floor.
func damagePreview(game, assets, path string) {
	const s = 128
	pipe := load(filepath.Join(game, "Data", "ships", "terran", "heat_pipe_adaptive", "floor_straight.png"))
	cable := load(filepath.Join(assets, "cable_body_straight.png"))
	anim := func(v, sfx string) *image.NRGBA { return load(filepath.Join(assets, "belt_anim", v+sfx+"_00.png")) }
	parts := []func(sfx string) *image.NRGBA{
		func(sfx string) *image.NRGBA { return anim("straight", sfx) },
		func(sfx string) *image.NRGBA { return anim("curve", sfx) },
		func(sfx string) *image.NRGBA { return load(filepath.Join(assets, "belt_connector"+sfx+".png")) },
		func(sfx string) *image.NRGBA { return load(filepath.Join(assets, "belt_splitter"+sfx+".png")) },
		func(sfx string) *image.NRGBA { return anim("heat_crossing", sfx) },
		func(sfx string) *image.NRGBA { return anim("cable_crossing", sfx) },
		func(sfx string) *image.NRGBA { return anim("plain_crossing", sfx) },
	}
	out := image.NewNRGBA(image.Rect(0, 0, 16+3*(2*s+16), 16+3*s+16+len(parts)*(s+16)))
	for i := range out.Pix {
		out.Pix[i] = 40
		if i%4 == 3 {
			out.Pix[i] = 255
		}
	}
	for k, c := range []struct {
		art      string
		up, down *image.NRGBA
	}{{"heat_crossing", pipe, pipe}, {"cable_crossing", cable, cable}, {"plain_crossing", nil, nil}, {"heat_cable_crossing", pipe, cable}, {"cable_heat_crossing", cable, pipe}} {
		x := 16 + k*(s+16)
		for row, im := range []*image.NRGBA{c.up, anim(c.art, ""), c.down} {
			if im != nil {
				paste(out, resize(im, s, s), x, 16+row*s)
			}
		}
	}
	for r, part := range parts {
		y := 16 + 3*s + 16 + r*(s+16)
		x := 16
		for _, sfx := range damageSuffix {
			im := part(sfx)
			paste(out, resize(im, im.Rect.Dx()*2, im.Rect.Dy()*2), x, y)
			x += im.Rect.Dx()*2 + 16
		}
	}
	save(out, path)
	damageScroll(game, assets, strings.TrimSuffix(path, filepath.Ext(path))+".gif")
}

// damageScroll animates at game scale (2x GIF tops 4 MB), one row per damage level 33 / 66: a damaged straight, curve and
// heat crossing (an undamaged one below it), each between undamaged belts over vanilla corridor floor, to check the surface damage scrolls and the joins stay seamless.
func damageScroll(game, assets, gifPath string) {
	floor := resize(load(filepath.Join(game, "Data", "ships", "terran", "corridor", "floor.png")), 64, 64)
	var frames []*image.NRGBA
	for k := range animFrames {
		a := func(v, sfx string) *image.NRGBA {
			return load(filepath.Join(assets, "belt_anim", fmt.Sprintf("%s%s_%02d.png", v, sfx, k)))
		}
		const W, H = 7, 6
		out := image.NewNRGBA(image.Rect(0, 0, W*64, H*64))
		for ty := range H {
			for tx := range W {
				paste(out, floor, tx*64, ty*64)
			}
		}
		for r, sfx := range damageSuffix[1:] {
			y := 3*r + 1
			put := func(im *image.NRGBA, tx, ty int) { paste(out, im, tx*64, (y+ty)*64) }
			flow := rotateCW(a("straight", "")) // belt flowing right
			put(a("straight", ""), 0, -1)
			put(a("straight", sfx), 0, 0)
			put(a("straight", ""), 0, 1)
			put(flow, 1, 1)
			put(a("curve", sfx), 2, 1)
			put(a("straight", ""), 2, 0)
			for i, s := range []string{sfx, ""} {
				put(flow, 4, i)
				put(a("heat_crossing", s), 5, i)
				put(flow, 6, i)
			}
		}
		frames = append(frames, out)
	}
	saveGIF(frames, gifPath)
}
