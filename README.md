# LingoLens

Penerjemah subtitle & teks layar game *real-time* untuk Windows, ditulis langsung menggunakan C# native (`.NET Framework 4.8` + `Windows.Media.Ocr`). Tanpa runtime tambahan, tanpa Python, dan tidak butuh akses Administrator.

[![Release](https://img.shields.io/badge/release-v0.3--beta-FFD23F?style=flat-square&labelColor=16181C)](https://github.com/danzyyy19/LingoLens/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-3DDBC0?style=flat-square&labelColor=16181C)](https://github.com/danzyyy19/LingoLens/releases/latest)
[![License](https://img.shields.io/badge/license-MIT-8B929E?style=flat-square&labelColor=16181C)](LICENSE)

---

## Unduh (v0.3 Beta)

Ambil biner siap pakai di halaman [**GitHub Releases**](https://github.com/danzyyy19/LingoLens/releases/latest):

| File | Keterangan |
| :--- | :--- |
| [**`LingoLens-Portable.exe`**](https://github.com/danzyyy19/LingoLens/releases/latest/download/LingoLens-Portable.exe) | Versi portable tunggal. Simpan di folder mana saja dan jalankan langsung (`settings.ini` tersimpan di folder yang sama). |
| [**`Setup-LingoLens.exe`**](https://github.com/danzyyy19/LingoLens/releases/latest/download/Setup-LingoLens.exe) | Installer Windows lengkap dengan shortcut Desktop, Start Menu, dan Uninstaller. |

---

## Apa yang Baru di v0.3 Beta

- **Penjernih OCR Subtitle Tanpa Background (Temporal 2-Frame + High-Pass):** Menghapus gangguan visual latar 3D yang bergerak (cahaya, partikel, nebula) tanpa merusak guratan font tipis, dilengkapi *auto-crop* pita teks aktif untuk area tangkap lebar (1440p/4K).
- **Kunci Jendela Game / Aplikasi:** Mengunci pembacaan OCR hanya pada proses game atau jendela video yang dipilih. Saat berpindah jendela (`Alt+Tab`), pembacaan OCR otomatis dijeda dan overlay disembunyikan.
- **Pengunci Subtitle Statis & Anti-Flip-Flop:** Mengunci hasil terjemahan berbasis kesamaan piksel huruf dan *character trigram* agar arti tidak berubah-ubah selama dialog yang sama masih tampil di layar.
- **Mode Bahasa Gaul (`Gue/Lo`), Santai, dan Baku:** Pilihan gaya bahasa terjemahan Indonesia yang menyesuaikan percakapan game sehari-hari.
- **Pencari Lore Game & Auto-Glosarium:** Tersedia 14 preset game populer secara offline ditambah pencarian ringkasan cerita & nama karakter otomatis dari Wikipedia untuk memperbaiki salah baca OCR pada istilah/nama khusus.
- **Riwayat Dialog Sesi:** Mencatat percakapan yang sudah diterjemahkan selama sesi berjalan untuk dibaca ulang atau disalin.
- **Antarmuka & Modal Baru (High-DPI Ready):** Tema gelap/terang dengan pilihan latar pola arsiran diagonal, grid, atau titik, lengkap dengan skala otomatis untuk layar DPI tinggi (125%–200%).

---

## Cara Pakai Singkat

1. Buka game atau video dalam mode **Windowed** atau **Borderless Windowed**.
2. Jalankan `LingoLens-Portable.exe` (atau buka dari shortcut hasil install).
3. *(Opsional)* Klik **Kunci Jendela Game** di sidebar kiri agar OCR hanya berjalan saat jendela game tersebut aktif, lalu pilih judul game di **Pilih Game & Lore** untuk memuat glosarium nama karakter.
4. Klik **Blok Layar OCR** (`Alt+Q`) dan tarik kotak pada area munculnya subtitle.
5. Klik **Blok Layar Hasil** (`Alt+A`) untuk mengatur posisi kotak overlay terjemahan.
6. Tekan **Mulai Live** (`Alt+E`).

### Pintasan Keyboard

| Pintasan | Fungsi |
| :--- | :--- |
| `Alt + E` | Mulai / hentikan penerjemahan live |
| `Alt + Q` | Blok area tangkap OCR di layar |
| `Alt + A` | Blok posisi kotak hasil terjemahan |
| `Alt + W` | Geser / ubah ukuran kotak langsung di layar |
| `F11` | Mode layar penuh jendela aplikasi |
| `ESC` | Batalkan seleksi blok layar |

---

## Build dari Source

Kompilasi hanya memakai `csc.exe` bawaan `.NET Framework 4.8` di Windows 10/11 (tidak perlu instal Visual Studio):

```powershell
git clone https://github.com/danzyyy19/LingoLens.git
cd LingoLens
.\build.ps1
```

File hasil kompilasi akan keluar di folder `dist\`:
- `dist\LingoLens-Portable.exe`
- `dist\Setup-LingoLens.exe`
- `dist\LingoLens.exe`

---

## Lisensi

Dirilis dengan lisensi [MIT](LICENSE) — dibuat oleh [Danzyyy](https://github.com/danzyyy19).
