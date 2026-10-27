// Command belt-sprites bakes the belt/splitter/connector sprites (docs/BELTS_DESIGN.md §11) from the Codex-generated
// masters in art/ by mechanical post-processing only: trim the magenta key bands, area-downscale, compose. No pixel is drawn.
//
//	cd tools\belt-sprites
//	go run . -import <dir with raw Codex *_master.png>   // one-time: key out bands, shrink to 256 px/tile into art/
//	go run .                                             // bake art/ -> ..\..\mod\assets
//	go run . -preview out.png                            // also compose new sprites next to vanilla ones
//	go run . -overhaul out.png                           // §21 cases in game layering + animated out.gif
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
	// Crossings: masters are drawn like belt.png (belt up, heat pipe / cable entering at the left and right edges under
	// the belt); baked rotated 90 degrees clockwise into the part frame (belt flows right, network on Up/Down).
	{"belt_heat_crossing", 1, 1, "belt_heat_crossing.png", "belt_heat_crossing_icon.png", "belt_heat_crossing_blueprint.png"},
	{"belt_cable_crossing", 1, 1, "belt_cable_crossing.png", "belt_cable_crossing_icon.png", "belt_cable_crossing_blueprint.png"},
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
	f, err := os.Open(path)
	if err != nil {
		fail(err)
	}
	defer f.Close()
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
	f, err := os.Create(path)
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
		for x := 0; x < w; x++ {
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
	return img.SubImage(image.Rect(0, y0, w, y1)).(*image.NRGBA)
}

// resize is an alpha-weighted area average (exact for integer downscales, nearest-area otherwise).
func resize(img *image.NRGBA, w, h int) *image.NRGBA {
	out := image.NewNRGBA(image.Rect(0, 0, w, h))
	ox, oy := img.Rect.Min.X, img.Rect.Min.Y
	sw, sh := img.Rect.Dx(), img.Rect.Dy()
	for y := 0; y < h; y++ {
		y0, y1 := y*sh/h, max((y+1)*sh/h, y*sh/h+1)
		for x := 0; x < w; x++ {
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

// rotateCW turns a square image 90 degrees clockwise (top row -> right column).
func rotateCW(img *image.NRGBA) *image.NRGBA {
	n := img.Rect.Dx()
	out := image.NewNRGBA(image.Rect(0, 0, n, n))
	for y := 0; y < n; y++ {
		for x := 0; x < n; x++ {
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
		row1 = append(row1, load(filepath.Join(assets, sp.file)))
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

// crossings lays out, at 2x game scale, each crossing in the part frame (rotation 0: belt flows right) between two
// horizontal belts, with the vanilla heat pipe / our electric cable straight sprite above and below it.
func crossings(game, assets, path string) {
	const s = 128
	pipe := load(filepath.Join(game, "Data", "ships", "terran", "heat_pipe_adaptive", "floor_straight.png"))
	cable := load(filepath.Join(assets, "cable_body_straight.png"))
	belt := rotateCW(load(filepath.Join(assets, "belt.png")))
	out := image.NewNRGBA(image.Rect(0, 0, 7*s+48, 3*s+32))
	for i := range out.Pix {
		out.Pix[i] = 40
		if i%4 == 3 {
			out.Pix[i] = 255
		}
	}
	for k, c := range []struct {
		cross string
		net   *image.NRGBA
	}{{"belt_heat_crossing.png", pipe}, {"belt_cable_crossing.png", cable}} {
		ox := 16 + k*(3*s+16)
		put := func(im *image.NRGBA, tx, ty int) { paste(out, resize(im, s, s), ox+tx*s, 16+ty*s) }
		put(c.net, 1, 0)
		put(c.net, 1, 2)
		put(belt, 0, 1)
		put(load(filepath.Join(assets, c.cross)), 1, 1)
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
	for y := 0; y < n; y++ {
		copy(out.Pix[y*out.Stride+surf0*4:y*out.Stride+surf1*4], img.Pix[((y+d)%n)*img.Stride+surf0*4:((y+d)%n)*img.Stride+surf1*4])
	}
	return out
}

// curveEdges makes the 64 px curve edges equal the straight frame's edges exactly (the bilinear polar remap is only
// approximate near the corner): front row = straight front row, left column = straight back row (radius = column).
func curveEdges(c, s *image.NRGBA) {
	n := c.Rect.Dx()
	for i := 0; i < n; i++ {
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
	for y := 0; y < n; y++ {
		for x := 0; x < n; x++ {
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
	for y := 0; y < masterTile; y++ {
		for x := surf0; x < surf1; x++ {
			out.SetNRGBA(x, y, frame.NRGBAAt(x, y))
		}
	}
	return out
}

// animVariants: output prefix -> builder from straight frame k. Order and names are wired in mod/belt.rules
// BeltSurfaceSprites and belts/src/BeltRender.cs (Variant).
func animVariants(artDir string) []struct {
	name  string
	build func(*image.NRGBA) *image.NRGBA
} {
	heat, cable := load(filepath.Join(artDir, "belt_heat_crossing.png")), load(filepath.Join(artDir, "belt_cable_crossing.png"))
	clone := func(f *image.NRGBA) *image.NRGBA { o := image.NewNRGBA(f.Rect); copy(o.Pix, f.Pix); return o }
	return []struct {
		name  string
		build func(*image.NRGBA) *image.NRGBA
	}{
		{"straight", func(f *image.NRGBA) *image.NRGBA { return f }},
		{"join_l", func(f *image.NRGBA) *image.NRGBA { o := clone(f); join(o, f, true); return o }},
		{"join_r", func(f *image.NRGBA) *image.NRGBA { o := clone(f); join(o, f, false); return o }},
		{"join_lr", func(f *image.NRGBA) *image.NRGBA { o := clone(f); join(o, f, true); join(o, f, false); return o }},
		{"curve", curveLeft},
		// Crossings: baked into the part frame like their static sprites (belt flows right).
		{"heat_crossing", func(f *image.NRGBA) *image.NRGBA { return rotateCW(pasteBand(heat, f)) }},
		{"cable_crossing", func(f *image.NRGBA) *image.NRGBA { return rotateCW(pasteBand(cable, f)) }},
	}
}

// bakeAnim writes <out>/belt_anim/<variant>_<k>.png (64 px) for k < animFrames; frame 0 of curve also replaces belt_curve.png.
func bakeAnim(artDir, outDir string) {
	belt := load(filepath.Join(artDir, "belt.png"))
	dir := filepath.Join(outDir, "belt_anim")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		fail(err)
	}
	for _, v := range animVariants(artDir) {
		for k := 0; k < animFrames; k++ {
			// Moving up the screen = texture rows scroll up: frame k shows the texture k/animFrames of a tile further on.
			f := shiftBand(belt, k*masterTile/animFrames)
			m := resize(v.build(f), 64, 64)
			if v.name == "curve" {
				curveEdges(m, resize(f, 64, 64))
			}
			save(m, filepath.Join(dir, fmt.Sprintf("%s_%02d.png", v.name, k)))
			if v.name == "curve" && k == 0 {
				save(m, filepath.Join(outDir, "belt_curve.png"))
			}
		}
	}
}

// flipH mirrors an image left-right (curve right = mirrored curve left, as flipU at draw time).
func flipH(img *image.NRGBA) *image.NRGBA {
	w, h := img.Rect.Dx(), img.Rect.Dy()
	out := image.NewNRGBA(image.Rect(0, 0, w, h))
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
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
	room = resize(room.SubImage(image.Rect(0, 0, room.Rect.Dx()/2, room.Rect.Dy()/2)).(*image.NRGBA), 64, 64)
	for ty := 0; ty < H; ty++ {
		for tx := 0; tx < W; tx++ {
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
	anim := &gif.GIF{}
	for k := 0; k < animFrames; k++ {
		f := overhaul(game, assets, k, k&2 == 0)
		p := image.NewPaletted(f.Rect, palette.Plan9)
		draw.FloydSteinberg.Draw(p, f.Rect, f, image.Point{})
		anim.Image = append(anim.Image, p)
		anim.Delay = append(anim.Delay, 3) // 1/32 s per frame is below GIF resolution; 30 ms
	}
	gifPath := strings.TrimSuffix(pngPath, filepath.Ext(pngPath)) + ".gif"
	f, err := os.Create(gifPath)
	if err != nil {
		fail(err)
	}
	if err := gif.EncodeAll(f, anim); err != nil {
		fail(err)
	}
	if err := f.Close(); err != nil {
		fail(err)
	}
	fmt.Println("wrote", gifPath, animFrames, "frames")
}

func main() {
	game := flag.String("game", `D:\Steam\steamapps\common\Cosmoteer`, "game root (preview only)")
	artDir := flag.String("art", "art", "master directory")
	outDir := flag.String("out", filepath.Join("..", "..", "mod", "assets"), "output directory")
	importDir := flag.String("import", "", "raw Codex output directory to import into -art first")
	previewPath := flag.String("preview", "", "optional preview PNG path")
	overhaulPath := flag.String("overhaul", "", "optional §21 overhaul preview PNG path (+ animated .gif next to it)")
	crossingsPath := flag.String("crossings", "", "optional crossings preview PNG path (belts left/right, pipe or cable above/below)")
	flag.Parse()

	if *importDir != "" {
		// Only masters present in -import are replaced, so one part can be regenerated without touching the others.
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
	for _, sp := range sprites {
		m := load(filepath.Join(*artDir, sp.master+".png"))
		if strings.HasSuffix(sp.master, "_crossing") {
			m = rotateCW(m)
		}
		save(resize(m, sp.w*64, sp.h*64), filepath.Join(*outDir, sp.file))
		if sp.blueprint != "" {
			save(blueprintOf(resize(m, sp.w*64, sp.h*64)), filepath.Join(*outDir, sp.blueprint))
		}
		if sp.icon != "" {
			save(resize(m, sp.w*32, sp.h*32), filepath.Join(*outDir, sp.icon))
		}
	}
	bakeAnim(*artDir, *outDir)
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
}
