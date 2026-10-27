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
}

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
	out := image.NewNRGBA(image.Rect(0, 0, W, 2*s+48))
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
	save(out, path)
}

func main() {
	game := flag.String("game", `D:\Steam\steamapps\common\Cosmoteer`, "game root (preview only)")
	artDir := flag.String("art", "art", "master directory")
	outDir := flag.String("out", filepath.Join("..", "..", "mod", "assets"), "output directory")
	importDir := flag.String("import", "", "raw Codex output directory to import into -art first")
	previewPath := flag.String("preview", "", "optional preview PNG path")
	flag.Parse()

	if *importDir != "" {
		for _, sp := range sprites {
			raw := trimKeyRows(load(filepath.Join(*importDir, sp.master+"_master.png")))
			save(resize(raw, sp.w*masterTile, sp.h*masterTile), filepath.Join(*artDir, sp.master+".png"))
		}
	}
	for _, sp := range sprites {
		m := load(filepath.Join(*artDir, sp.master+".png"))
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
}
