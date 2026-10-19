import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// The output of this build is embedded inside FengSync.Lan.dll and served from two routes:
//   /p              -> dist/index.html, always no-store
//   /assets/{path}  -> dist/assets/*, immutable because every name carries its own hash
// Nothing here may reach another origin: the page is opened over plain HTTP on a private address, usually with no
// route to the internet at all, so a CDN is not a slow choice — it is a broken one.
export default defineConfig({
  base: "/",
  // public/ would land unhashed files in the root of dist, where the /assets route cannot reach them and the
  // immutable cache header would be a lie.
  publicDir: false,
  plugins: [react(), tailwindcss()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    assetsDir: "assets",
    sourcemap: false,
    target: "es2020",
    // Tailwind 4 emits @property and color-mix; Safari 16 is the oldest engine that has both, and a phone old
    // enough to miss them still gets the layout, just with the fallbacks the declarations already carry.
    cssTarget: "safari16",
    cssCodeSplit: false,
    reportCompressedSize: false,
    rollupOptions: {
      output: {
        // Every asset is named after its own content, which is what lets the listener answer with `immutable`.
        // There is no dynamic import anywhere in this application, so it is one script and one stylesheet.
        entryFileNames: "assets/[name]-[hash].js",
        chunkFileNames: "assets/[name]-[hash].js",
        assetFileNames: "assets/[name]-[hash][extname]",
      },
    },
  },
});
