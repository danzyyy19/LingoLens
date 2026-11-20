# Master Plan: Translator-Live C# Studio Edition (UI Overhaul)

Dokumen ini berisi rencana perombakan antarmuka (UI) dan arsitektur **Translator-Live C# Studio Edition** di `E:\PROJECT\Translator-live\translator-live-c#`.

## Fokus Utama Perombakan UI
1. **Bisa Fullscreen (`F11`) & Resizable Responsif**:
   - Jendela utama menggunakan layout *Responsive Bento Grid* yang otomatis menyesuaikan ukuran saat di-maximize, di-resize bebas, atau masuk ke **Mode Fullscreen (`F11` atau tombol Fullscreen di kanan atas)**.
2. **Anti-Burik (Per-Monitor DPI Awareness V2 + Pixel-Snapped Vector Rendering)**:
   - Setiap garis batas `1px`, teks (`ClearTypeGridFit`), dan ikon vektor digambar pada koordinat piksel fisik tanpa *bitmap stretching*.
3. **Tactile Pro Buttons (Bukan Tombol Flat Simpel)**:
   - Konstruksi 4-lapis: *Drop Shadow*, *Vertical Micro-Gradient*, *1px Top Specular Bevel Highlight*, *Physical Press Offset*, dilengkapi **Ikon Vektor Geometris** dan **Keycap Shortcut Badge** (`Alt+E`, `Alt+Q`, `Alt+W`, `Alt+R`, `F11`).
4. **Obsidian Studio Dark Mode & Alabaster Pro Light Mode**:
   - Kontras terukur dengan permukaan berlapis (*layered surfaces*), bukan sekadar hitam/putih polos.
5. **Interactive 16:9 Screen Radar Map**:
   - Peta visual layar monitor secara real-time di dalam aplikasi: Anda dapat melihat, memilih, menggeser, dan mengubah ukuran kotak **OCR (Biru)** serta **Kotak Hasil (Emas)** langsung di kanvas radar maupun di layar sesungguhnya.
