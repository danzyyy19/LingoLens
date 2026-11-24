# Planning & Audit Bug / Kekurangan LingoLens

Dokumen ini berisi hasil audit menyeluruh terhadap aplikasi **LingoLens** (`src/`, `installer/`, `build.ps1`), mencakup bug yang ditemukan, kekurangan arsitektur/UX/keamanan, serta rencana perbaikan bertahap.

---

## 1. Bug Kritis & Menengah (Prioritas Tinggi)

### 1.1. [FIXED] OCR Gagal Deteksi Font Putih + Border Hitam Tipis di Background Game Putih
- **Lokasi**: `src/TranslationEngine.cs` (`RunMultiPassWinRtOcr`, `ApplyDarkOutlineSubtitleIsolator`, `ComputeBrightTextMask`)
- **Penyebab**:
  - Ketika teks game berwarna putih dengan garis tepi (outline) hitam tipis berada di atas background game berwarna putih/sangat terang:
    - `ApplyDarkOutlineSubtitleIsolator` memicu mode `lightBoxDarkText` yang mengubah gambar menjadi outline tipis berongga (hollow wireframe) di atas putih, sehingga WinRT OCR gagal membaca huruf.
    - Jika `lightBoxDarkText` tidak terpicu, dilasi `hasDarkHalo` (radius 4px) melebar ke luar border ke area background putih, sehingga background putih di sekitar huruf ikut berubah jadi blok hitam pekat.
    - `ComputeBrightTextMask` memakai langkah `dx` hingga 5px yang bisa melompati border hitam tipis 1-2px.
- **Solusi (Sudah Diimplementasikan)**:
  - Menambahkan **Pass 4 (`ApplyThinDarkBorderFillEnhancer`)** berbasis *Morphological Closing* (Dilate -> Erode) pada piksel border gelap terhadap background terang lokal, mengisi bagian dalam huruf putih yang berongga menjadi tinta hitam solid di atas putih tanpa menutup lubang huruf (`O`, `e`, `a`, `D`).
  - Memperkecil `dx`/`dy` di `ComputeBrightTextMask` agar tidak melompati border tipis.
  - Menambahkan regression test `[OCR-WHITE-ON-WHITE-BORDER]` di `src/Program.cs`.

### 1.2. [FIXED] Hardcoded Path Developer di `--self-test`
- **Lokasi**: `src/Program.cs`
- **Solusi**: Path absolut developer dihapus, diganti dengan folder relatif `tests/fixtures/` dan env var opsional `LINGOLENS_AUDIT_DIR`.

### 1.3. [FIXED] API Key Gemini Disimpan Plaintext di `settings.ini` & Profil Game
- **Lokasi**: `src/AppConfig.cs` (`SaveToFile`, `SaveGameProfile`, `LoadFromFile`)
- **Solusi**: `geminiKey` kini dienkripsi dengan Windows DPAPI (`ProtectedData.Protect`, `DataProtectionScope.CurrentUser`) dengan prefix `dpapi:`, backward-compatible dengan config lama, dan tidak ikut disimpan saat menyimpan file profil game (`profiles/*.ini`).

### 1.4. Hardcoded Public API Key `helloworld` di Fallback OCR.space
- **Lokasi**: `src/TranslationEngine.cs` (`RecognizeOnlineOcrSpace`, baris 3105)
- **Masalah**: Menggunakan key publik demo `apikey=helloworld` yang sangat sering terkena rate-limit global.
- **Rencana Perbaikan**: Sediakan opsi input API key OCR.space di tab Sistem (atau tampilkan pesan panduan install Windows OCR Language Pack yang jauh lebih cepat & offline).

### 1.5. [FIXED] Silent `catch { }` yang Terlalu Banyak (Menyembunyikan Error)
- **Lokasi**: `src/AppLogger.cs`, `src/AppConfig.cs`, `src/Modals.cs`
- **Solusi**: Menambahkan `AppLogger` ringan dengan rotasi otomatis 512 KB (`debug.log`) dan menghubungkannya ke error penyimpanan/pemuatan config serta ekspor riwayat dialog.

---

## 2. Kekurangan Fungsional & Teknis (Prioritas Menengah)

### 2.1. [FIXED] Pemecahan Monolithic `TranslationEngine.cs`
- **Solusi**: `TranslationEngine.cs` (~3.800 baris) telah dipecah secara modular menggunakan `partial class TranslationEngine` menjadi 4 file terfokus:
  - `src/TranslationEngine.cs` (Worker loop, state, anti-flip-flop lock, orkestrasi)
  - `src/TranslationEngine.OcrPreprocessing.cs` (Temporal Eraser, High-Pass, Dark-Outline Isolator, Thin-Border Fill, WinRT OCR)
  - `src/TranslationEngine.TextProcessing.cs` (Scoring OCR, CleanGameOcrText, Naturalisasi Indonesia, Glossary)
  - `src/TranslationEngine.Providers.cs` (Google GTX, MyMemory, Gemini AI, Online OCR, JSON parser)

### 2.2. Keterbatasan Dukungan Non-English / CJK pada OCR Filter & Scoring
- **Lokasi**: `src/TranslationEngine.cs` (`ScoreOcrQuality`, `IsPlausibleOcrWord`, `IsMeaningfulSubtitle`)
- **Masalah**:
  - Untuk bahasa Jepang/Mandarin/Korea (`isCjk`), `IsPlausibleOcrWord` selalu mengembalikan `true` dan `ScoreOcrQuality` tidak mengecek rasio karakter CJK vs noise ASCII, sehingga noise 3D bisa lolos sebagai teks valid.
  - Bahasa Latin non-Inggris (Spanyol, Prancis, Jerman) terkena penalti karena `RemoveDiacritics` menghapus aksen dan `ScoreOcrQuality` mengecek daftar kata 2-huruf bahasa Inggris.
- **Rencana Perbaikan**: Buat scoring khusus per rumpun bahasa (CJK character ratio check, dan hindari penalti aksen untuk `es`/`fr`/`de`).

### 2.3. Hardcoded Game-Specific Rules (Zelda OoT) di Pipeline Global
- **Lokasi**: `src/TranslationEngine.cs` (`PreProcessGameIdioms` baris 1190-1218, `NaturalizeIndonesian` baris 1324-1346, `CleanGameOcrText` baris 3001-3012)
- **Masalah**: Aturan khusus *Zelda: Ocarina of Time* (mis. `Deku`, `Hyrule`, `Master Sword`, `frule -> Hyrule`) dijalankan untuk **semua** game. Ini bisa menyebabkan false-positive di game lain dan membuat kode tidak generik.
- **Rencana Perbaikan**: Pindahkan aturan istilah spesifik game ke sistem **Glossary per-profil** (`GameLoreService` / file `.ini`).

### 2.4. Capture Layar Tidak Mendukung Exclusive Fullscreen
- **Lokasi**: `src/TranslationEngine.cs` (`CaptureRect` via GDI `CopyFromScreen`)
- **Masalah**: GDI `CopyFromScreen` menghasilkan layar hitam pada game DirectX/Vulkan dalam mode *Exclusive Fullscreen* (hanya bekerja di *Borderless Windowed* / *Windowed*).
- **Rencana Perbaikan**: Tambahkan opsi/fallback capture menggunakan **Windows.Graphics.Capture (WGC)** atau DXGI Desktop Duplication untuk performa lebih rendah CPU dan dukungan layar penuh yang lebih baik.

### 2.5. [PARTIAL] Deteksi Konflik Hotkey Global & Custom Hotkey
- **Lokasi**: `src/MainWindow.cs` (`RegisterHotkeys`)
- **Status**: Deteksi konflik hotkey (`Alt+E/Q/W/R/H/A`) + log ke `AppLogger` + notifikasi toast sudah ditambahkan. UI untuk kustomisasi tombol hotkey masih bisa dikembangkan di versi mendatang.

### 2.6. [FIXED] Multi-Monitor & Per-Monitor DPI Scaling (`WM_DPICHANGED`)
- **Lokasi**: `src/MainWindow.cs` (`WndProc`)
- **Solusi**: Menambahkan handler `WM_DPICHANGED` agar `_dpiScale`, `MinimumSize`, dan ukuran jendela otomatis menyesuaikan saat dipindahkan antar-monitor dengan DPI berbeda.

---

## 3. Kekurangan UI / UX (Prioritas Rendah-Menengah)

### 3.1. Tidak Ada Preview Debug OCR (Untuk Troubleshooting User)
- **Masalah**: Ketika OCR gagal membaca font tertentu di game user, user tidak bisa melihat seperti apa hasil preprocessing gambar yang dibaca WinRT OCR.
- **Rencana Perbaikan**: Tambahkan tombol **"Tes & Lihat Hasil OCR"** di UI yang menampilkan preview gambar hasil preprocessing (Pass 1-4) beserta skor masing-masing pass.

### 3.2. [FIXED] Ekspor Riwayat Dialog (`.txt` / `.csv`)
- **Lokasi**: `src/Modals.cs` (`BuildDialogBacklogModal`)
- **Solusi**: Menambahkan tombol **Ekspor (.txt / .csv)** langsung di modal Riwayat Dialog agar user/streamer dapat menyimpan transkrip percakapan sesi.

### 3.3. [FIXED] Sinkronisasi Versi Otomatis di `UpdateChecker.cs`
- **Lokasi**: `src/UpdateChecker.cs`
- **Solusi**: `UpdateChecker.CurrentVersion` kini otomatis membaca `AssemblyInformationalVersionAttribute` dari assembly sehingga selalu sinkron dengan `Program.cs`.

---

## 4. QA Audit: Matriks Stress-Test Subtitle Game Sulit (Real-World Edge Cases)

Sebagai QA, pengujian OCR tidak cukup hanya dengan teks putih di kotak hitam. Berikut daftar kategori game nyata dengan subtitle paling sulit di-OCR beserta analisis titik lemah pipeline LingoLens saat ini (`src/TranslationEngine.cs`):

| No | Kategori Masalah Subtitle | Contoh Game Nyata | Penyebab Sulit untuk OCR | Titik Lemah / Bug di LingoLens Saat Ini | Rekomendasi Perbaikan |
|---|---|---|---|---|---|
| **1** | **Teks Berwarna / Neon (Kuning, Cyan, Emas, Merah)** | *Cyberpunk 2077*, *Genshin Impact*, *Hades*, *GTA V* (keyword kuning/biru), *FFXIV* | Saturasi warna tinggi (`sat > 52`), sering tanpa kotak gelap di belakangnya. | **BUG**: Pass 2 (`ApplyFontPreservingEnhancer`) menekan piksel `sat > 52`; `ComputeBrightTextMask` & `DetectActiveTextBand` mengabaikan `mx - mn > 86`; Pass 3 hanya mengekstrak teks warna jika `darkDominantBg` (>45% gelap). Teks kuning di siang hari bisa **hilang/terhapus**. | Tambah mode ekstraksi warna spesifik (kuning/cyan/emas) berbasis kontras lokal tanpa membuang saturasi tinggi, atau gunakan kanal `Max(R,G,B)` untuk teks warna terang. |
| **2** | **Font Putih + Drop Shadow 1 Arah di Background Putih/Salju** | *Red Dead Redemption 2* (Ambarino), *God of War Ragnarök* (Midgard), *NieR: Automata* (Copied City), *Zelda BotW/TotK* (Hebra) | Background putih (`#FFF`) menyatu dengan isi font; berbeda dari full-outline, *drop shadow* hanya gelap di sisi kanan-bawah. | Pass 4 (`ApplyThinDarkBorderFillEnhancer`) sudah memperbaiki **full outline tipis**, tetapi pada **drop-shadow 1 arah** sisi kiri-atas huruf tidak punya garis gelap sehingga *morphological closing* belum menutup penuh. | Tambahkan deteksi *unidirectional drop-shadow* (ekstrak siluet bayangan gelapnya saja lalu geser/tebalkan sebagai huruf hitam di atas putih). |
| **3** | **Font Pixelated / Retro & Teks Bergetar (Shaky Text)** | *Undertale*, *Celeste*, *Stardew Valley*, *Octopath Traveler*, *Chrono Trigger*, *Minecraft* | Huruf kotak-kotak resolusi rendah; di *Undertale*/*Celeste* huruf bergetar tiap frame. | Bilinear upscale (`ScaleAndProcessRawBgra`) membuat font pixel buram; teks bergetar membuat Pass 0 (`ApplyTemporalBackgroundEraser`) mengira huruf adalah background bergerak lalu **menghapus huruf**. | Tambahkan opsi/deteksi *Pixel Art Mode* (Nearest-Neighbor upscale 2x–3x) dan matikan Temporal Eraser otomatis jika terdeteksi getaran mikro (*micro-jitter*) pada area teks. |
| **4** | **Font Miring (Skewed/Italic) & Kaligrafi/Serif Tipis** | *Persona 5 Royal* (bubble miring), *Okami* (brush font), *Hollow Knight*, *Elden Ring*, *Borderlands* | WinRT OCR akurat untuk teks horizontal, tapi drop drastis pada kemiringan > 7° (*Persona 5*) dan serif sangat tipis (*Hollow Knight*/*Elden Ring*). | Belum ada *deskew* (pelurusan sudut teks); serif tipis masih rentan *split-letter* (`k`→`lc`, `m`→`rn`) di luar daftar kata hardcoded. | Tambahkan deteksi sudut/rotasi ringan (atau profil *deskew* per-game) dan perluas perbaikan ligature/serif berbasis kamus/bigram. |
| **5** | **Typewriter / Streaming Text (Huruf Muncul Bertahap)** | *Steins;Gate*, *Ace Attorney*, *Persona 5*, *Doki Doki Literature Club*, *Animal Crossing* | Teks muncul satu per satu; OCR terlalu cepat membaca kalimat belum selesai. | Default `InstantSubtitleMode = true` + *Anti-Flip-Flop Lock* dapat mengunci terjemahan di **setengah kalimat pertama** jika pertambahan kata < 2 kata per tick. | Auto-deteksi pola *typewriter* (teks terus memanjang di prefix yang sama dalam 300ms) agar otomatis menunggu kalimat selesai meski user lupa mematikan `InstantSubtitleMode`. |
| **6** | **Teks Gelap di Kertas Perkamen / Bertekstur / Emas Tua** | *Pentiment*, *Skyrim* (buku), *Baldur's Gate 3* (surat), *The Witcher 3* (bestiary) | Background kekuningan/cokelat bertekstur (`sat > 48`, `lum < 182`). | Kondisi `lightBoxDarkText` di Pass 3 (`avgLum >= 182 && avgSat <= 48 && avgMinC >= 165`) **tidak terpicu** pada perkamen kuning/cokelat tua, sehingga teks hitam gagal dibaca. | Ganti syarat `lightBoxDarkText` dengan *Adaptive Local Thresholding* (Sauvola/Otsu lokal) untuk mendeteksi tinta gelap di atas background medium-terang bertekstur. |
| **7** | **Dialog Panjang Multi-Paragraf & Furigana / Teks Vertikal** | *Disco Elysium* (kolom sempit scrolling), *Yakuza / Like a Dragon*, Visual Novel Jepang (Furigana & vertikal) | Kolom dialog terus menggulir dari bawah; *furigana* kecil di atas kanji terbaca sebagai baris acak. | `MergeLines` menggabungkan paragraf lama & baru jadi satu teks raksasa; belum ada filter tinggi baris (*bounding-box height filter*) untuk membuang furigana kecil. | Gunakan `OcrWord.BoundingRect.Height` untuk menyaring *furigana* (baris dengan tinggi < 50% tinggi kanji utama) dan deteksi jeda paragraf vertikal. |

### Daftar Test Case QA yang Wajib Ditambahkan ke `--self-test`
Untuk mencegah regresi di setiap rilis, `--self-test` perlu dilengkapi synthetic benchmark untuk:
1. `[QA-WHITE-ON-WHITE-OUTLINE]` — Font putih + border hitam tipis (`1.2px`) di background `#FFFFFF` *(sudah ada)*.
2. `[QA-WHITE-ON-WHITE-DROPSHADOW]` — Font putih + drop shadow 1 arah (tanpa outline) di background salju/putih.
3. `[QA-COLORED-SUBTITLE-BRIGHT-BG]` — Font kuning/cyan (*Cyberpunk 2077* / *Genshin*) di background terang.
4. `[QA-PIXEL-FONT]` — Font pixel 8-bit/16-bit (*Undertale* / *Stardew Valley*).
5. `[QA-PARCHMENT-DARK-INK]` — Tinta cokelat/hitam di atas perkamen kuning bertekstur (*Skyrim* / *BG3*).
6. `[QA-TYPEWRITER-STREAM]` — Simulasi 6 frame teks muncul bertahap (harus hanya emit 1x saat kalimat utuh).

---

## 5. Roadmap Pelaksanaan

| Fase | Fokus | Item Utama |
|---|---|---|
| **Fase 1 (Selesai / Sekarang)** | Perbaikan Bug OCR Kritis | Fix white-on-white bordered font (`ApplyThinDarkBorderFillEnhancer`), tambah regression test di `--self-test`. |
| **Fase 2** | Stabilitas & Keamanan | Enkripsi `geminiKey` (DPAPI), hapus path hardcoded developer di `Program.cs`, tambah file logging ringan, pisahkan `geminiKey` dari profil game. |
| **Fase 3** | QA Edge-Case OCR & Akurasi | Fix teks berwarna/neon di background terang (*Cyberpunk*/*Genshin*), fix perkamen bertekstur (*BG3*/*Skyrim*), support drop-shadow 1 arah (*RDR2*/*GoW*), auto-deteksi *typewriter*, perbaiki scoring CJK & furigana filter. |
| **Fase 4** | Refactoring, Fitur & UX | Pecah `TranslationEngine.cs` ke modul terpisah, pindahkan rule spesifik Zelda ke Glossary, Custom Hotkey + deteksi konflik, Preview Debug OCR, Ekspor Riwayat Dialog, Per-Monitor DPI & dukungan WGC capture. |
