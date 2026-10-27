// Command belt-sprites bakes the temporary belt/splitter/connector art (docs/BELTS_DESIGN.md §11) from vanilla
// game PNGs by mechanical post-processing only: darken, tint, scale, rotate 90°, compose. No pixel is drawn.
//
//	cd tools\belt-sprites; go run . -game D:\Steam\steamapps\common\Cosmoteer -out ..\..\mod\assets
package main

import (
	"flag"
	"fmt"
	"image"
	"image/color"
	"image/png"
	"os"
	"path/filepath"
)

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
	fmt.Println("wrote", path)
}

func fail(err error) {
	fmt.Fprintln(os.Stderr, err)
	os.Exit(1)
}

// darken multiplies RGB by k.
func darken(img *image.NRGBA, k float64) *image.NRGBA {
	out := image.NewNRGBA(img.Rect)
	for i := 0; i < len(img.Pix); i += 4 {
		out.Pix[i] = uint8(float64(img.Pix[i]) * k)
		out.Pix[i+1] = uint8(float64(img.Pix[i+1]) * k)
		out.Pix[i+2] = uint8(float64(img.Pix[i+2]) * k)
		out.Pix[i+3] = img.Pix[i+3]
	}
	return out
}

// resize is a box/bilinear-free area average (exact for integer downscales, nearest-area otherwise).
func resize(img *image.NRGBA, w, h int) *image.NRGBA {
	out := image.NewNRGBA(image.Rect(0, 0, w, h))
	sw, sh := img.Rect.Dx(), img.Rect.Dy()
	for y := 0; y < h; y++ {
		y0, y1 := y*sh/h, max((y+1)*sh/h, y*sh/h+1)
		for x := 0; x < w; x++ {
			x0, x1 := x*sw/w, max((x+1)*sw/w, x*sw/w+1)
			var r, g, b, a, n float64
			for sy := y0; sy < y1; sy++ {
				for sx := x0; sx < x1; sx++ {
					c := img.NRGBAAt(sx, sy)
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

// rotate90 rotates clockwise n quarter turns.
func rotate90(img *image.NRGBA, n int) *image.NRGBA {
	for ; n > 0; n-- {
		w, h := img.Rect.Dx(), img.Rect.Dy()
		out := image.NewNRGBA(image.Rect(0, 0, h, w))
		for y := 0; y < h; y++ {
			for x := 0; x < w; x++ {
				out.SetNRGBA(h-1-y, x, img.NRGBAAt(x, y))
			}
		}
		img = out
	}
	return img
}

// overlay alpha-blends src (tinted by tint, alpha scaled by op) onto dst at (ox, oy).
func overlay(dst, src *image.NRGBA, ox, oy int, tint color.NRGBA, op float64) {
	for y := 0; y < src.Rect.Dy(); y++ {
		for x := 0; x < src.Rect.Dx(); x++ {
			s := src.NRGBAAt(x, y)
			a := float64(s.A) / 255 * op * float64(tint.A) / 255
			if a <= 0 {
				continue
			}
			d := dst.NRGBAAt(ox+x, oy+y)
			mix := func(dc, sc, tc uint8) uint8 {
				return uint8(float64(dc)*(1-a) + float64(sc)*float64(tc)/255*a)
			}
			da := float64(d.A) / 255
			dst.SetNRGBA(ox+x, oy+y, color.NRGBA{mix(d.R, s.R, tint.R), mix(d.G, s.G, tint.G), mix(d.B, s.B, tint.B), uint8((a + da*(1-a)) * 255)})
		}
	}
}

func side(a, b *image.NRGBA) *image.NRGBA {
	out := image.NewNRGBA(image.Rect(0, 0, a.Rect.Dx()+b.Rect.Dx(), a.Rect.Dy()))
	overlay(out, a, 0, 0, color.NRGBA{255, 255, 255, 255}, 1)
	overlay(out, b, a.Rect.Dx(), 0, color.NRGBA{255, 255, 255, 255}, 1)
	return out
}

func main() {
	game := flag.String("game", `D:\Steam\steamapps\common\Cosmoteer`, "game root")
	outDir := flag.String("out", filepath.Join("mod", "assets"), "output directory")
	// Vanilla arrow icons point right (+X, checked visually); 3 clockwise turns make them point up = part front (rules (0,-1)).
	arrowTurns := flag.Int("arrow-turns", 3, "clockwise quarter turns applied to vanilla arrow icons")
	flag.Parse()
	data := filepath.Join(*game, "Data")
	in := func(p string) *image.NRGBA { return load(filepath.Join(data, p)) }
	out := func(img *image.NRGBA, name string) { save(img, filepath.Join(*outDir, name)) }
	amber := color.NRGBA{235, 170, 40, 255}
	cyan := color.NRGBA{60, 200, 235, 255}

	// Belt: vanilla conveyor plates, darker than corridor and conveyor, amber vanilla chevron arrow for direction.
	belt := darken(in(`ships\terran\conveyor\conveyor1.png`), 0.45)
	overlay(belt, rotate90(resize(in(`roof_decals\icons\icon_arrow_chevrons.png`), 40, 40), *arrowTurns), 12, 12, amber, 0.8)
	out(belt, "belt.png")
	out(resize(belt, 32, 32), "belt_icon.png")

	// Splitter 1x2: two belt tiles plus the vanilla split-arrow icon across both.
	splitter := side(darken(in(`ships\terran\conveyor\conveyor1.png`), 0.45), darken(in(`ships\terran\conveyor\conveyor1.png`), 0.45))
	overlay(splitter, rotate90(resize(in(`roof_decals\icons\icon_arrow_head_split.png`), 56, 56), *arrowTurns), 36, 4, amber, 0.85)
	out(splitter, "belt_splitter.png")
	out(resize(splitter, 64, 32), "belt_splitter_icon.png")
	bp := in(`ships\terran\conveyor\blueprints.png`)
	out(side(bp, bp), "belt_splitter_blueprint.png")

	// Connector: dark corridor floor with a cyan vanilla thick arrow (back -> front).
	connector := darken(in(`ships\terran\corridor\floor.png`), 0.35)
	overlay(connector, rotate90(resize(in(`roof_decals\icons\icon_arrow_thick.png`), 44, 44), *arrowTurns), 10, 10, cyan, 0.9)
	out(connector, "belt_connector.png")
	out(resize(connector, 32, 32), "belt_connector_icon.png")
}
