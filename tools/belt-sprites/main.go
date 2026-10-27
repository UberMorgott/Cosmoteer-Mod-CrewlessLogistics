// Command belt-sprites bakes the belt/splitter/connector sprites (docs/BELTS_DESIGN.md §11) from the Codex-generated
// masters in art/ by mechanical post-processing only: trim the magenta key bands, area-downscale, compose. No pixel is drawn.
//
//	cd tools\belt-sprites
//	go run . -import <dir with raw Codex *_master.png>   // one-time: key out bands, shrink to 256 px/tile into art/
//	go run .                                             // bake art/ -> ..\..\mod\assets
//	go run . -preview out.png                            // also compose new sprites next to vanilla ones
package main

import (
	"flag"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
	"strings"
)

// masterTile is the stored master resolution per tile; the game uses 64 px per tile.
const masterTile = 256

// sprite: master name, size in tiles (w, h), output names for the 64 px/tile sprite and its editor icon (32 px/tile).
type sprite struct {
	master     string
	w, h       int
	file, icon string
}

var sprites = []sprite{
	{"belt", 1, 1, "belt.png", "belt_icon.png"},
	// shortcut: curve sprite is baked but not wired (floor art is static per part); needs a C# shape-driven floor quad.
	{"belt_curve", 1, 1, "belt_curve.png", ""},
	{"belt_connector", 1, 1, "belt_connector.png", "belt_connector_icon.png"},
	{"belt_splitter", 2, 1, "belt_splitter.png", "belt_splitter_icon.png"},
	{"belt_splitter_blueprint", 2, 1, "belt_splitter_blueprint.png", ""},
	// Crossings: masters are drawn like belt.png (belt up, heat pipe / cable entering at the left and right edges under
	// the belt); baked rotated 90 degrees clockwise into the part frame (belt flows right, network on Up/Down).
	{"belt_heat_crossing", 1, 1, "belt_heat_crossing.png", "belt_heat_crossing_icon.png"},
	{"belt_cable_crossing", 1, 1, "belt_cable_crossing.png", "belt_cable_crossing_icon.png"},
}

// layers are animation-ready masters kept in art/ only (not baked yet): <part>_base = housing with moving parts removed,
// <part>_moving = rollers/shuttle/shutters/lights alone on transparency. The game uses the composed <part> master.
var layers = []sprite{
	{master: "belt_connector_base", w: 1, h: 1}, {master: "belt_connector_moving", w: 1, h: 1},
	{master: "belt_splitter_base", w: 2, h: 1}, {master: "belt_splitter_moving", w: 2, h: 1},
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

func main() {
	game := flag.String("game", `D:\Steam\steamapps\common\Cosmoteer`, "game root (preview only)")
	artDir := flag.String("art", "art", "master directory")
	outDir := flag.String("out", filepath.Join("..", "..", "mod", "assets"), "output directory")
	importDir := flag.String("import", "", "raw Codex output directory to import into -art first")
	previewPath := flag.String("preview", "", "optional preview PNG path")
	crossingsPath := flag.String("crossings", "", "optional crossings preview PNG path (belts left/right, pipe or cable above/below)")
	flag.Parse()

	if *importDir != "" {
		// Only masters present in -import are replaced, so one part can be regenerated without touching the others.
		for _, sp := range append(sprites, layers...) {
			path := filepath.Join(*importDir, sp.master+"_master.png")
			if !exists(path) {
				continue
			}
			raw := load(path)
			if base, ok := strings.CutSuffix(sp.master, "_moving"); ok {
				// The overlay is mostly magenta, so crop it to the base layer's key bands and key magenta to alpha.
				b := trimKeyRows(load(filepath.Join(*importDir, base+"_base_master.png"))).Rect
				raw = raw.SubImage(image.Rect(0, b.Min.Y, raw.Rect.Dx(), b.Max.Y)).(*image.NRGBA)
				for i := 0; i < len(raw.Pix); i += 4 {
					if magenta(color.NRGBA{raw.Pix[i], raw.Pix[i+1], raw.Pix[i+2], 255}) {
						raw.Pix[i+3] = 0
					}
				}
			} else {
				raw = trimKeyRows(raw)
			}
			save(resize(raw, sp.w*masterTile, sp.h*masterTile), filepath.Join(*artDir, sp.master+".png"))
		}
	}
	for _, sp := range sprites {
		m := load(filepath.Join(*artDir, sp.master+".png"))
		if strings.HasSuffix(sp.master, "_crossing") {
			m = rotateCW(m)
		}
		if strings.HasSuffix(sp.master, "_blueprint") {
			// Vanilla blueprints are translucent blue (pale ~A76, lane ~A165): key alpha from the red channel.
			for i := 0; i < len(m.Pix); i += 4 {
				m.Pix[i+3] = uint8(float64(255-m.Pix[i]) * 0.8)
			}
		}
		save(resize(m, sp.w*64, sp.h*64), filepath.Join(*outDir, sp.file))
		if sp.icon != "" {
			save(resize(m, sp.w*32, sp.h*32), filepath.Join(*outDir, sp.icon))
		}
	}
	if *previewPath != "" {
		preview(*game, *outDir, *previewPath)
	}
	if *crossingsPath != "" {
		crossings(*game, *outDir, *crossingsPath)
	}
}
