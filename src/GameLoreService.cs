using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TranslatorLive
{
    public class GameLoreEntry
    {
        public string Title = "";
        public string Genre = "";
        public string DefaultSrc = "en";
        public string LoreSummary = "";
        public string Glossary = "";
        public string SourceUrl = "";
        public bool IsOnlineResult = false;
    }

    public static class GameLoreService
    {
        private static readonly List<GameLoreEntry> _presets = BuildPresets();

        public static List<GameLoreEntry> GetPresets()
        {
            return new List<GameLoreEntry>(_presets);
        }

        public static GameLoreEntry FindPreset(string query)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string q = query.Trim().ToLowerInvariant();
            if (q == "oot" || q == "zelda oot" || q.Contains("ocarina")) return FindByKey("Ocarina of Time");
            if (q == "totk" || q == "zelda totk" || q.Contains("tears")) return FindByKey("Tears of the Kingdom");
            if (q == "p5" || q == "p5r" || q.Contains("persona 5")) return FindByKey("Persona 5");
            if (q == "er" || q.Contains("elden")) return FindByKey("Elden Ring");
            if (q == "ff7" || q == "ffvii" || q.Contains("final fantasy vii")) return FindByKey("Final Fantasy VII");
            if (q == "rdr2" || q == "rdr 2" || q.Contains("red dead")) return FindByKey("Red Dead Redemption 2");
            if (q == "gtav" || q == "gta5" || q == "gta 5" || q == "gta v") return FindByKey("Grand Theft Auto V");
            if (q == "tw3" || q == "witcher 3" || q.Contains("witcher")) return FindByKey("Witcher 3");
            if (q == "cp77" || q == "cp2077" || q.Contains("cyberpunk")) return FindByKey("Cyberpunk 2077");
            if (q == "gow" || q == "gowr" || q.Contains("ragnarok") || q.Contains("god of war")) return FindByKey("God of War");
            if (q == "hk" || q.Contains("hollow knight")) return FindByKey("Hollow Knight");
            if (q == "bg3" || q.Contains("baldur")) return FindByKey("Baldur's Gate 3");
            if (q == "re4" || q.Contains("resident evil 4")) return FindByKey("Resident Evil 4");
            if (q == "gi" || q.Contains("genshin")) return FindByKey("Genshin Impact");

            foreach (GameLoreEntry e in _presets)
            {
                if (e.Title.ToLowerInvariant() == q) return e;
            }
            foreach (GameLoreEntry e in _presets)
            {
                if (e.Title.ToLowerInvariant().Contains(q)) return e;
            }
            return null;
        }

        private static GameLoreEntry FindByKey(string key)
        {
            foreach (GameLoreEntry e in _presets)
                if (e.Title.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return e;
            return null;
        }

        private static List<GameLoreEntry> BuildPresets()
        {
            List<GameLoreEntry> list = new List<GameLoreEntry>();

            list.Add(new GameLoreEntry
            {
                Title = "The Legend of Zelda: Ocarina of Time",
                Genre = "Action RPG",
                DefaultSrc = "en",
                LoreSummary = "Link, anak dari Kokiri Forest yang ditemani peri Navi, dipanggil Great Deku Tree buat menghentikan Ganondorf yang mau merebut Triforce dan menguasai kerajaan Hyrule. Lewat Ocarina of Time dan Master Sword di Temple of Time, Link melintasi masa kecil dan dewasa untuk membangunkan Seven Sages.",
                Glossary = "Hyrule;Link;Zelda;Ganondorf;Ganon;Navi;Saria;Kokiri;Kokiri Forest;Great Deku Tree;Deku;Triforce;Master Sword;Ocarina of Time;Temple of Time;Sheik;Impa;Rauru;Darunia;Goron;Ruto;Zora;Nabooru;Gerudo;Kakariko;Lon Lon Ranch;Epona;Malon;Death Mountain;Sacred Realm"
            });

            list.Add(new GameLoreEntry
            {
                Title = "The Legend of Zelda: Tears of the Kingdom",
                Genre = "Action Adventure",
                DefaultSrc = "en",
                LoreSummary = "Setelah Demon King Ganondorf bangkit di bawah Hyrule Castle, Princess Zelda menghilang ke masa lalu dan langit Hyrule dipenuhi Sky Islands. Link memakai tangan kanan Rauru (Zonai) untuk mencari para Sage, mengungkap sejarah Imprisoning War, dan memperbaiki Master Sword.",
                Glossary = "Hyrule;Link;Zelda;Ganondorf;Rauru;Sonia;Mineru;Purah;Tulin;Sidon;Yunobo;Riju;Zonai;Ultrahand;Recall;Ascend;Fuse;Master Sword;Sky Islands;Depths;Gloom;Secret Stone;Lookout Landing;Korok;Hestu;Yiga;Master Kohga;Demon King"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Persona 5 Royal",
                Genre = "JRPG / Social Sim",
                DefaultSrc = "en",
                LoreSummary = "Siswa SMA di Tokyo bernama Joker membentuk Phantom Thieves of Hearts bersama Ryuji, Ann, dan Morgana. Mereka masuk ke Metaverse untuk mencuri Treasure dari Palace orang-orang korup biar hati mereka berubah, sambil menjalani kehidupan sekolah dan Confidant.",
                Glossary = "Joker;Phantom Thieves;Metaverse;Palace;Mementos;Morgana;Ryuji;Ann;Yusuke;Makoto;Futaba;Haru;Akechi;Kasumi;Sumire;Maruki;Sojiro;Leblanc;Velvet Room;Igor;Persona;Shadow;Confidant;Shibuya;Shujin;Treasure;Calling Card"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Elden Ring",
                Genre = "Action RPG / Soulslike",
                DefaultSrc = "en",
                LoreSummary = "Di Lands Between, Elden Ring telah hancur dalam peristiwa Shattering yang memicu perang antar Demigod. Kamu bermain sebagai Tarnished yang dipandu Grace dan Melina buat mengumpulkan Great Runes, menuju Erdtree, dan menjadi Elden Lord.",
                Glossary = "Lands Between;Tarnished;Elden Ring;Elden Lord;Erdtree;Melina;Marika;Radagon;Godfrey;Malenia;Radahn;Ranni;Blaidd;Iji;Godrick;Rennala;Rykard;Morgott;Mohg;Maliketh;Messmer;Miquella;Roundtable Hold;Site of Grace;Great Rune;Demigod;Outer Gods;Finger Maiden"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Final Fantasy VII Remake / Rebirth",
                Genre = "Action JRPG",
                DefaultSrc = "en",
                LoreSummary = "Cloud Strife, mantan tentara SOLDIER bayaran, bergabung dengan grup Avalanche (Barret, Tifa) di kota Midgar buat melawan perusahaan Shinra yang menyedot energi Lifestream/Mako. Pertemuan dengan Aerith dan kembalinya Sephiroth menyeret mereka ke konflik nasib planet.",
                Glossary = "Cloud;Tifa;Aerith;Barret;Sephiroth;Zack;Red XIII;Yuffie;Cait Sith;Vincent;Cid;Shinra;Midgar;Avalanche;SOLDIER;Mako;Lifestream;Materia;Jenova;Cetra;Ancients;Whispers;Buster Sword;Gold Saucer;Kalm;Cosmo Canyon;Nibelheim"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Red Dead Redemption 2",
                Genre = "Action Adventure / Western",
                DefaultSrc = "en",
                LoreSummary = "Tahun 1899 di Amerika, era koboi mulai tamat. Arthur Morgan, tangan kanan Dutch van der Linde, harus bertahan hidup bersama geng Van der Linde sambil dikejar agen Pinkerton, menghadapi pengkhianatan Micah, dan mencari penebusan di tengah kehancuran geng.",
                Glossary = "Arthur Morgan;Dutch;John Marston;Sadie Adler;Charles;Hosea;Micah;Abigail;Jack;Lenny;Javier;Bill;Uncle;Van der Linde;Pinkerton;O'Driscoll;Blackwater;Valentine;Saint Denis;Rhodes;Strawberry;Guarma;Dead Eye;Lumbago"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Grand Theft Auto V",
                Genre = "Open World Action",
                DefaultSrc = "en",
                LoreSummary = "Di kota Los Santos, tiga kriminal berbeda latar belakang — mantan perampok pensiunan Michael, anak jalanan ambisius Franklin, dan psikopat liar Trevor — terjebak kerjasama perampokan besar sambil ditekan agen korup FIB dan miliarder licik.",
                Glossary = "Michael;Franklin;Trevor;Lester;Lamar;Amanda;Jimmy;Tracey;Dave Norton;Steve Haines;Devin Weston;Los Santos;Blaine County;Vinewood;FIB;IAA;Merryweather;Grove Street;Ballas;Chop"
            });

            list.Add(new GameLoreEntry
            {
                Title = "The Witcher 3: Wild Hunt",
                Genre = "Open World RPG",
                DefaultSrc = "en",
                LoreSummary = "Geralt of Rivia, seorang Witcher pemburu monster, keliling benua yang dilanda perang Nilfgaard vs Northern Realms buat mencari anak angkatnya Ciri yang diburu pasukan hantu Wild Hunt. Dibantu Yennefer, Triss, Vesemir, dan Dandelion.",
                Glossary = "Geralt;Ciri;Yennefer;Triss;Vesemir;Dandelion;Zoltan;Eredin;Wild Hunt;Witcher;Kaer Morhen;Novigrad;Velen;Skellige;Nilfgaard;Redania;Temeria;Emhyr;Roach;Gwent;Signs;Igni;Quen;Axii;Aard;Yrden;Bloody Baron"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Cyberpunk 2077",
                Genre = "Sci-Fi Action RPG",
                DefaultSrc = "en",
                LoreSummary = "Di kota futuristik Night City, tentara bayaran V mengalami kecelakaan saat mencuri biochip Relic milik korporasi Arasaka. Di dalam kepala V kini hidup engram rocker pemberontak Johnny Silverhand yang perlahan mengambil alih otaknya.",
                Glossary = "Night City;Johnny Silverhand;Arasaka;Militech;Relic;Jackie;Judy;Panam;River;Kerry;Takemura;Rogue;Alt Cunningham;Adam Smasher;Viktor;Misty;Songbird;Solomon Reed;Dogtown;Netrunner;Cyberware;Choom;Edgerunner;Corpo;Nomad;Braindance;MaxTac"
            });

            list.Add(new GameLoreEntry
            {
                Title = "God of War Ragnarök",
                Genre = "Action Adventure",
                DefaultSrc = "en",
                LoreSummary = "Fimbulwinter melanda Nine Realms menandai datangnya Ragnarök. Kratos dan anaknya Atreus (Loki) mencari jawaban soal ramalan Giant sambil berhadapan dengan Odin, Thor, dan Freya, ditemani Mimir serta kurcaci Brok dan Sindri.",
                Glossary = "Kratos;Atreus;Loki;Mimir;Freya;Odin;Thor;Heimdall;Tyr;Angrboda;Brok;Sindri;Faye;Jormungandr;Fenrir;Ragnarok;Fimbulwinter;Midgard;Asgard;Alfheim;Svartalfheim;Vanaheim;Jotunheim;Helheim;Muspelheim;Niflheim;Leviathan Axe;Blades of Chaos"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Hollow Knight",
                Genre = "Metroidvania",
                DefaultSrc = "en",
                LoreSummary = "Seorang kesatria serangga bisu (The Knight) turun ke reruntuhan kerajaan bawah tanah Hallownest yang dilanda wabah mimpi Infection dari Radiance. Ia bertemu Hornet, Quirrel, dan para Dreamer untuk menentukan nasib Hollow Knight.",
                Glossary = "Hallownest;Hollow Knight;Hornet;Pale King;Radiance;Infection;Dreamers;Quirrel;Cornifer;Iselda;Elderbug;Zote;Grimm;Dirtmouth;Greenpath;City of Tears;Deepnest;Crystal Peak;White Palace;Abyss;Vessel;Soul;Geo;Nail;Charm"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Baldur's Gate 3",
                Genre = "CRPG / Fantasy",
                DefaultSrc = "en",
                LoreSummary = "Setelah diculik Mind Flayer dan ditanami parasit tadpole di otak, karakter utama bersama Shadowheart, Astarion, Gale, Lae'zel, Wyll, dan Karlach berpetualang di Faerûn buat mencari obat sekaligus mengungkap konspirasi sekte The Absolute.",
                Glossary = "Baldur's Gate;Faerun;Shadowheart;Astarion;Gale;Lae'zel;Wyll;Karlach;Halsin;Minthara;Jaheira;Minsc;Withers;Mind Flayer;Illithid;Githyanki;Absolute;Nautiloid;Tadpole;Tiefling;Emerald Grove;Moonrise Towers;Orin;Gortash;Ketheric;Shar;Selune"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Resident Evil 4",
                Genre = "Survival Horror",
                DefaultSrc = "en",
                LoreSummary = "Agen khusus AS Leon S. Kennedy dikirim ke desa terpencil di Spanyol buat menyelamatkan anak presiden, Ashley Graham, yang diculik sekte fanatik Los Iluminados pimpinan Osmund Saddler yang mengendalikan penduduk lewat parasit Las Plagas.",
                Glossary = "Leon;Ashley;Ada Wong;Luis Sera;Saddler;Salazar;Krauser;Bitores Mendez;Hunnigan;Merchant;Los Iluminados;Las Plagas;Ganado;Umbrella;Wesker;Raccoon City"
            });

            list.Add(new GameLoreEntry
            {
                Title = "Genshin Impact",
                Genre = "Action RPG",
                DefaultSrc = "en",
                LoreSummary = "Traveler yang terpisah dari saudara kembarnya menjelajahi tujuh bangsa di dunia Teyvat bersama Paimon, bertemu para Archon dan menghadapi organisasi Fatui serta Abyss Order demi mengungkap kebenaran Celestia.",
                Glossary = "Teyvat;Traveler;Paimon;Archon;Fatui;Harbingers;Abyss Order;Celestia;Vision;Gnosis;Mondstadt;Liyue;Inazuma;Sumeru;Fontaine;Natlan;Snezhnaya;Venti;Zhongli;Raiden Shogun;Nahida;Furina;Mavuika;Dainsleif;Hilichurl"
            });

            return list;
        }

        /// <summary>
        /// Searches Wikipedia API (English) for ANY game title, extracts lore summary + auto-detected character/place glossary,
        /// and translates summary to Indonesian (or uses Gemini if key is configured).
        /// </summary>
        public static GameLoreEntry SearchOnlineLore(string gameQuery, string geminiApiKey, out string statusMsg)
        {
            statusMsg = "";
            string q = (gameQuery ?? "").Trim();
            if (string.IsNullOrEmpty(q))
            {
                statusMsg = "Masukkan nama game dulu.";
                return null;
            }

            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls12;
            }
            catch { }

            try
            {
                // 1. Search Wikipedia for "<game> video game" first, then fallback to "<game>"
                string wikiTitle = FindBestWikiTitle(q + " video game");
                if (string.IsNullOrEmpty(wikiTitle))
                    wikiTitle = FindBestWikiTitle(q);

                if (string.IsNullOrEmpty(wikiTitle))
                {
                    statusMsg = "Game '" + q + "' tidak ditemukan di Wikipedia. Cek ejaan judul game.";
                    return null;
                }

                // 2. Fetch plain-text extract (intro + plot/setting) from Wikipedia API
                string extractUrl = "https://en.wikipedia.org/w/api.php?action=query&prop=extracts&exintro=false&explaintext=true&exchars=3200&redirects=1&format=json&titles=" + Uri.EscapeDataString(wikiTitle);
                string json = HttpGetString(extractUrl, 7000);
                if (string.IsNullOrEmpty(json))
                {
                    statusMsg = "Gagal mengambil data lore dari internet (koneksi terputus/timeout).";
                    return null;
                }

                string extract = ExtractJsonStringValue(json, "extract");
                string resolvedTitle = ExtractJsonStringValue(json, "title");
                if (string.IsNullOrEmpty(resolvedTitle)) resolvedTitle = wikiTitle;

                // Strip "(video game)" suffix for clean display
                string displayTitle = Regex.Replace(resolvedTitle, @"\s*\([^)]*video game[^)]*\)", "", RegexOptions.IgnoreCase).Trim();
                if (string.IsNullOrEmpty(displayTitle)) displayTitle = q;

                if (string.IsNullOrEmpty(extract))
                {
                    statusMsg = "Halaman Wikipedia ditemukan, tapi ringkasan lore kosong.";
                    return null;
                }

                // Focus on Intro + Plot/Story/Setting sections
                string focusedText = BuildFocusedLoreExcerpt(extract);

                // Extract proper nouns for Glossary automatically
                string autoGlossary = ExtractProperNounsGlossary(focusedText, displayTitle);

                // Translate or summarize lore into crisp Indonesian
                string indoLore = "";
                if (!string.IsNullOrEmpty(geminiApiKey))
                {
                    string aiSummary, aiGlossary;
                    if (TryGeminiLoreSynthesis(displayTitle, focusedText, geminiApiKey, out aiSummary, out aiGlossary))
                    {
                        indoLore = aiSummary;
                        if (!string.IsNullOrEmpty(aiGlossary)) autoGlossary = MergeGlossaries(aiGlossary, autoGlossary);
                    }
                }

                if (string.IsNullOrEmpty(indoLore))
                {
                    string shortEn = focusedText.Length > 620 ? focusedText.Substring(0, 620) : focusedText;
                    int lastDot = shortEn.LastIndexOf('.');
                    if (lastDot > 240) shortEn = shortEn.Substring(0, lastDot + 1);
                    string tr = QuickTranslateToIndo(shortEn);
                    indoLore = !string.IsNullOrEmpty(tr) ? tr : shortEn;
                }

                GameLoreEntry entry = new GameLoreEntry();
                entry.Title = displayTitle;
                entry.Genre = "Online Lore (Wikipedia)";
                entry.DefaultSrc = DetectLikelySrcLang(displayTitle, focusedText);
                entry.LoreSummary = indoLore.Trim();
                entry.Glossary = autoGlossary;
                entry.SourceUrl = "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(resolvedTitle.Replace(' ', '_'));
                entry.IsOnlineResult = true;

                statusMsg = "Lore ditemukan: " + displayTitle + " (" + CountTerms(autoGlossary) + " istilah otomatis)";
                return entry;
            }
            catch (Exception ex)
            {
                statusMsg = "Gagal cari lore online: " + ex.Message;
                return null;
            }
        }

        private static string FindBestWikiTitle(string searchQuery)
        {
            string url = "https://en.wikipedia.org/w/api.php?action=query&list=search&srlimit=5&format=json&srsearch=" + Uri.EscapeDataString(searchQuery);
            string json = HttpGetString(url, 6000);
            if (string.IsNullOrEmpty(json)) return "";

            MatchCollection titles = Regex.Matches(json, "\"title\":\"((?:\\\\.|[^\"])*)\"");
            MatchCollection snippets = Regex.Matches(json, "\"snippet\":\"((?:\\\\.|[^\"])*)\"");

            string firstTitle = "";
            for (int i = 0; i < titles.Count; i++)
            {
                string t = UnescapeJson(titles[i].Groups[1].Value);
                string snip = (i < snippets.Count) ? UnescapeJson(snippets[i].Groups[1].Value).ToLowerInvariant() : "";
                if (i == 0) firstTitle = t;
                string tl = t.ToLowerInvariant();
                if (tl.Contains("video game") || snip.Contains("video game") || snip.Contains("role-playing") ||
                    snip.Contains("action-adventure") || snip.Contains("developed by") || snip.Contains("published by"))
                {
                    return t;
                }
            }
            return firstTitle;
        }

        private static string BuildFocusedLoreExcerpt(string rawExtract)
        {
            string clean = rawExtract.Replace("\r\n", "\n").Trim();
            string[] sections = Regex.Split(clean, @"\n==+\s*");
            StringBuilder sb = new StringBuilder();
            if (sections.Length > 0)
            {
                string intro = sections[0].Trim();
                if (intro.Length > 900) intro = intro.Substring(0, 900);
                sb.AppendLine(intro);
            }

            for (int i = 1; i < sections.Length; i++)
            {
                string sec = sections[i];
                int endHeader = sec.IndexOf("==");
                if (endHeader <= 0) continue;
                string hdr = sec.Substring(0, endHeader).Trim().ToLowerInvariant();
                string body = sec.Substring(endHeader + 2).Trim();
                if (hdr.Contains("plot") || hdr.Contains("story") || hdr.Contains("synopsis") ||
                    hdr.Contains("setting") || hdr.Contains("premise") || hdr.Contains("characters"))
                {
                    if (body.Length > 1200) body = body.Substring(0, 1200);
                    sb.AppendLine(body);
                    if (sb.Length > 2200) break;
                }
            }

            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        public static string ExtractProperNounsGlossary(string text, string gameTitle)
        {
            if (string.IsNullOrEmpty(text)) return "";

            HashSet<string> stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "The", "This", "That", "These", "Those", "There", "Their", "They", "When", "Where", "While",
                "With", "From", "Into", "After", "Before", "During", "Under", "Over", "Between", "Through",
                "Game", "Video", "Player", "Players", "Story", "Plot", "Setting", "Gameplay", "Series", "Release",
                "Released", "Developed", "Published", "Directed", "Written", "Windows", "PlayStation", "Xbox",
                "Nintendo", "Switch", "Steam", "Microsoft", "Sony", "PC", "Console", "Edition", "Remake", "Remaster",
                "Sequel", "Prequel", "Chapter", "Act", "Part", "World", "Action", "Adventure", "Role", "Playing",
                "First", "Second", "Third", "January", "February", "March", "April", "May", "June", "July",
                "August", "September", "October", "November", "December", "English", "Japanese", "North", "America",
                "Europe", "Japan", "United", "States", "However", "Although", "Because", "Meanwhile", "Later",
                "Eventually", "Following", "Upon", "Within", "Without", "Despite", "Unlike", "According", "Including",
                "Also", "Many", "Some", "Most", "More", "Other", "Such", "Each", "Both", "Only", "Main", "New",
                "Old", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "His", "Her",
                "Its", "Who", "What", "Which", "Whose", "Whom", "And", "But", "For", "Not", "Are", "Was", "Were"
            };

            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Match capitalized single or multi-word proper nouns NOT at sentence start (or repeated >= 2 times)
            MatchCollection ms = Regex.Matches(text, @"(?<=[a-z,;:\(]\s+)([A-Z][a-zA-Z0-9'\-]{2,}(?:\s+[A-Z][a-zA-Z0-9'\-]{2,}){0,2})");
            foreach (Match m in ms)
            {
                string cand = m.Groups[1].Value.Trim().TrimEnd('.', ',', ';', ':', ')', '\'');
                if (cand.Length < 3 || cand.Length > 32) continue;
                if (cand.EndsWith("'s")) cand = cand.Substring(0, cand.Length - 2);
                string firstWord = cand.Split(' ')[0];
                if (stop.Contains(cand) || stop.Contains(firstWord)) continue;
                if (Regex.IsMatch(cand, @"^\d+$")) continue;

                int c;
                counts.TryGetValue(cand, out c);
                counts[cand] = c + 1;
            }

            List<KeyValuePair<string, int>> ranked = new List<KeyValuePair<string, int>>(counts);
            ranked.Sort(delegate(KeyValuePair<string, int> a, KeyValuePair<string, int> b)
            {
                int cmp = b.Value.CompareTo(a.Value);
                if (cmp != 0) return cmp;
                return b.Key.Length.CompareTo(a.Key.Length);
            });

            List<string> selected = new List<string>();
            foreach (KeyValuePair<string, int> kv in ranked)
            {
                if (selected.Count >= 28) break;
                bool dup = false;
                foreach (string existing in selected)
                {
                    if (string.Equals(existing, kv.Key, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                }
                if (!dup) selected.Add(kv.Key);
            }

            return string.Join(";", selected.ToArray());
        }

        private static bool TryGeminiLoreSynthesis(string gameTitle, string wikiText, string apiKey, out string indoSummary, out string glossary)
        {
            indoSummary = "";
            glossary = "";
            try
            {
                string url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key=" + Uri.EscapeDataString(apiKey);
                string prompt = "Kamu adalah asisten lore gamer Indonesia. Berdasarkan teks Wikipedia untuk game \"" + gameTitle + "\" berikut:\n" +
                    wikiText + "\n\n" +
                    "Berikan output PERSIS dalam 2 baris format ini (tanpa markdown bold/bintang):\n" +
                    "LORE: [ringkasan cerita/dunia game ini dalam 2-3 kalimat bahasa Indonesia santai/gaul]\n" +
                    "GLOSSARY: [daftar 15-25 nama karakter, lokasi, faksi, dan item penting dipisahkan titik koma ;]";

                string escaped = prompt.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
                string payload = "{\"contents\":[{\"parts\":[{\"text\":\"" + escaped + "\"}]}],\"generationConfig\":{\"temperature\":0.2,\"maxOutputTokens\":420}}";

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Timeout = 8000;
                byte[] body = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = body.Length;
                using (Stream st = req.GetRequestStream()) st.Write(body, 0, body.Length);

                string resp;
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    resp = sr.ReadToEnd();

                Match m = Regex.Match(resp, "\"text\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
                if (!m.Success) return false;
                string raw = UnescapeJson(m.Groups[1].Value);
                foreach (string line in raw.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string tr = line.Trim();
                    if (tr.StartsWith("LORE:", StringComparison.OrdinalIgnoreCase))
                        indoSummary = tr.Substring(5).Trim();
                    else if (tr.StartsWith("GLOSSARY:", StringComparison.OrdinalIgnoreCase))
                        glossary = tr.Substring(9).Trim();
                }
                return !string.IsNullOrEmpty(indoSummary);
            }
            catch { return false; }
        }

        private static string QuickTranslateToIndo(string text)
        {
            try
            {
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=id&dt=t&q=" + Uri.EscapeDataString(text);
                string raw = HttpGetString(url, 5000);
                if (string.IsNullOrEmpty(raw)) return "";
                MatchCollection ms = Regex.Matches(raw, "\\[\"((?:\\\\.|[^\"])*)\",\"(?:\\\\.|[^\"])*\"");
                StringBuilder sb = new StringBuilder();
                foreach (Match m in ms) sb.Append(UnescapeJson(m.Groups[1].Value));
                return sb.ToString().Trim();
            }
            catch { return ""; }
        }

        private static string DetectLikelySrcLang(string title, string text)
        {
            // Most PC/Console releases played with English subtitles default to "en"
            return "en";
        }

        public static string MergeGlossaries(string primary, string secondary)
        {
            List<string> list = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] sources = new string[] { primary ?? "", secondary ?? "" };
            foreach (string src in sources)
            {
                foreach (string part in src.Split(new char[] { ';', '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = part.Trim();
                    if (t.Length < 2) continue;
                    if (seen.Add(t)) list.Add(t);
                }
            }
            return string.Join(";", list.ToArray());
        }

        public static int CountTerms(string glossary)
        {
            if (string.IsNullOrEmpty(glossary)) return 0;
            return glossary.Split(new char[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static string HttpGetString(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "LingoLens/0.4 (GameLoreFetcher; Windows)";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        private static string ExtractJsonStringValue(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
            if (!m.Success) return "";
            return UnescapeJson(m.Groups[1].Value);
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else if (n == 't') sb.Append('\t');
                    else if (n == '"' || n == '\\' || n == '/') sb.Append(n);
                    else if (n == 'u' && i + 4 < s.Length)
                    {
                        int code;
                        if (int.TryParse(s.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                    }
                    else sb.Append(n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }
    }
}
