using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace TranslatorLive
{
    public partial class TranslationEngine
    {
        public static string ExtractSpeakerTag(string text, out string speaker)
        {
            speaker = "";
            if (string.IsNullOrWhiteSpace(text)) return text;
            string s = text.Trim();

            // [Saria] Dialog...
            Match mBracket = Regex.Match(s, @"^\[([A-Za-z0-9 '\-]{2,24})\]\s*(.+)$", RegexOptions.Singleline);
            if (mBracket.Success && mBracket.Groups[2].Value.Trim().Length > 0)
            {
                speaker = mBracket.Groups[1].Value.Trim();
                return mBracket.Groups[2].Value.Trim();
            }

            // Saria: Dialog... (Exclude common non-speaker English prefixes like Note:, Warning:, Tip:, Objective:, Quest:, Chapter:, Location:, Hint:, Error:, Mission:, System:)
            Match mColon = Regex.Match(s, @"^([A-Z\p{Lu}][A-Za-z0-9 '\-]{1,20}):\s+(.+)$", RegexOptions.Singleline);
            if (mColon.Success && mColon.Groups[2].Value.Trim().Length > 0)
            {
                string cand = mColon.Groups[1].Value.Trim();
                if (!Regex.IsMatch(cand, @"^(?:http|https|note|notes|warning|caution|tip|tips|hint|hints|quest|objective|mission|chapter|act|part|stage|level|floor|room|area|location|place|time|date|day|year|status|effect|skill|item|weapon|armor|reward|bonus|tutorial|system|error|info|notice|rule|rules|goal|target|task|step|option|result|summary)$", RegexOptions.IgnoreCase))
                {
                    speaker = cand;
                    return mColon.Groups[2].Value.Trim();
                }
            }

            // Two-line: "Saria\nYou should hurry..." (Only extract if first line is not a common conversational opener/greeting)
            int nl = s.IndexOf('\n');
            if (nl > 1 && nl < 22)
            {
                string first = s.Substring(0, nl).Trim();
                string rest = s.Substring(nl + 1).Trim();
                if (first.Length >= 2 && first.Length <= 20 && char.IsUpper(first[0]) &&
                    !first.Contains(".") && !first.Contains("!") && !first.Contains("?") && !first.Contains(",") &&
                    first.Split(' ').Length <= 2 && rest.Length > first.Length &&
                    !Regex.IsMatch(first, @"^(?:Hello|Hi|Hey|Well|Now|So|Wait|Listen|Look|Stop|Go|Come|Run|Help|Please|Thanks|Thank\s+you|Sorry|Excuse\s+me|Yes|No|Okay|Alright|Right|Sure|Fine|Good|Great|What|Why|Where|When|Who|How|Here|There|This|That|These|Those|Watch\s+out|Hold\s+on|Hang\s+on|Be\s+careful|Come\s+on|Let's\s+go|Hurry|Hurry\s+up|By\s+the\s+way|Of\s+course|No\s+way|Not\s+yet|Once\s+upon|Long\s+ago|One\s+day|At\s+last|Finally|Suddenly|Meanwhile|However|Therefore|Besides|Anyway|Actually|Really|Seriously|Indeed|Perhaps|Maybe)$", RegexOptions.IgnoreCase))
                {
                    speaker = first;
                    return rest;
                }
            }

            return s;
        }

        private class GlossaryEntry
        {
            public string Source;
            public string Target;
            public string Token;
        }

        private static List<GlossaryEntry> ParseGlossary(string glossary)
        {
            List<GlossaryEntry> list = new List<GlossaryEntry>();
            if (string.IsNullOrWhiteSpace(glossary)) return list;
            string[] parts = glossary.Replace("\r\n", "\n").Split(new char[] { ';', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int idx = 0;
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length < 2) continue;
                int eq = p.IndexOf('=');
                string src = eq > 0 ? p.Substring(0, eq).Trim() : p;
                string tgt = eq > 0 ? p.Substring(eq + 1).Trim() : p;
                if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) continue;
                list.Add(new GlossaryEntry { Source = src, Target = tgt, Token = "ZXLLG" + (idx++) + "XZ" });
            }
            list.Sort(delegate (GlossaryEntry a, GlossaryEntry b) { return b.Source.Length.CompareTo(a.Source.Length); });
            return list;
        }

        private static string ProtectGlossary(string text, List<GlossaryEntry> entries)
        {
            if (entries == null || entries.Count == 0 || string.IsNullOrEmpty(text)) return text;
            string s = text;
            foreach (GlossaryEntry e in entries)
                s = Regex.Replace(s, @"\b" + Regex.Escape(e.Source) + @"\b", e.Token, RegexOptions.IgnoreCase);
            return s;
        }

        private static string RestoreGlossary(string text, List<GlossaryEntry> entries)
        {
            if (entries == null || entries.Count == 0 || string.IsNullOrEmpty(text)) return text;
            string s = text;
            foreach (GlossaryEntry e in entries)
                s = Regex.Replace(s, Regex.Escape(e.Token), e.Target, RegexOptions.IgnoreCase);
            return s;
        }

        private static string PreProcessGameIdioms(string text, string srcLang, string tgtLang, bool naturalTone)
        {
            if (!naturalTone || tgtLang != "id") return text;
            if (srcLang != "en" && srcLang != "auto") return text;

            string s = text;
            // Catch idioms & follow up
            s = Regex.Replace(s, @"\b[Cc]atch\s+(?:me\s+)?up\s+later\b", "meet me later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+later\b", "meet up later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+with\s+me\b", "follow and meet me");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+with\s+us\b", "follow and meet us");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\s+with\s+you\b", "meet you later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+(?:you|ya)\s+later\b", "see you later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+me\s+later\b", "meet me later");
            s = Regex.Replace(s, @"\b[Cc]atch\s+me\s+if\s+you\s+can\b", "chase me if you can");
            s = Regex.Replace(s, @"\b[Cc]atch\s+me\s+up\b", "tell me what happened");
            s = Regex.Replace(s, @"\b[Cc]atch\s+up\b", "meet up");
            s = Regex.Replace(s, @"\b(?:I'll|I\s+will)\s+catch\s+up\b", "I will follow you");
            s = Regex.Replace(s, @"\b[Dd]id\s+you\s+catch\s+that\b", "did you understand that");

            // Game & combat dialogue expressions
            s = Regex.Replace(s, @"\bI('ve|\s+have)?\s+got\s+this\b", "leave this to me");
            s = Regex.Replace(s, @"\bI('ve|\s+have)?\s+got\s+your\s+back\b", "I will protect you");
            s = Regex.Replace(s, @"\bI('ve|\s+have)?\s+got\s+it\b", "I understand");
            s = Regex.Replace(s, @"\b[Hh]ang\s+in\s+there\b", "stay strong");
            s = Regex.Replace(s, @"\b[Hh]old\s+on\s+a\s+(?:sec|second|minute|moment)\b", "wait a moment");
            s = Regex.Replace(s, @"\b[Hh]old\s+on\b", "wait a minute");
            s = Regex.Replace(s, @"\b[Hh]ang\s+on\b", "wait a minute");
            s = Regex.Replace(s, @"\b[Ww]atch\s+out\b", "be careful");
            s = Regex.Replace(s, @"\b[Ll]ook\s+out\b", "be careful");
            s = Regex.Replace(s, @"\b[Hh]eads\s+up\b", "be careful");
            s = Regex.Replace(s, @"\b[Ww]hat's\s+up\b", "what is happening");
            s = Regex.Replace(s, @"\b[Ww]hat's\s+wrong\b", "what is the problem");
            s = Regex.Replace(s, @"\b[Ww]hat's\s+the\s+matter\b", "what is the problem");
            s = Regex.Replace(s, @"\b[Ww]hat\s+are\s+you\s+up\s+to\b", "what are you doing");
            s = Regex.Replace(s, @"\b[Ww]hat('s|\s+is)\s+going\s+on\b", "what is happening");
            s = Regex.Replace(s, @"\b[Nn]o\s+way\b", "impossible");
            s = Regex.Replace(s, @"\b[Aa]re\s+you\s+kidding(?:\s+me)?\b", "are you joking");
            s = Regex.Replace(s, @"\b[Aa]re\s+you\s+for\s+real\b", "are you serious");
            s = Regex.Replace(s, @"\b[Pp]iece\s+of\s+cake\b", "very easy");
            s = Regex.Replace(s, @"\b[Gg]ive\s+me\s+a\s+break\b", "spare me");
            s = Regex.Replace(s, @"\b[Dd]on't\s+sweat\s+it\b", "do not worry");
            s = Regex.Replace(s, @"\b[Nn]o\s+worries\b", "do not worry");
            s = Regex.Replace(s, @"\b[Tt]ake\s+care\b", "be safe");
            s = Regex.Replace(s, @"\b[Nn]ever\s+mind\b", "forget it");
            s = Regex.Replace(s, @"\b[Yy]ou\s+bet\b", "of course");
            s = Regex.Replace(s, @"\b[Mm]ake\s+up\s+your\s+mind\b", "decide quickly");
            s = Regex.Replace(s, @"\b[Cc]ut\s+it\s+out\b", "stop doing that");
            s = Regex.Replace(s, @"\b[Gg]ive\s+it\s+a\s+shot\b", "try it");
            s = Regex.Replace(s, @"\b[Gg]ive\s+it\s+a\s+try\b", "try it");
            s = Regex.Replace(s, @"\b[Ss]hut\s+up\b", "be quiet");
            s = Regex.Replace(s, @"\b[Gg]et\s+lost\b", "go away");
            s = Regex.Replace(s, @"\b[Bb]eat\s+it\b", "go away");
            s = Regex.Replace(s, @"\b[Bb]ack\s+off\b", "step back");
            s = Regex.Replace(s, @"\b[Tt]ake\s+it\s+easy\b", "relax");
            s = Regex.Replace(s, @"\b[Ff]air\s+enough\b", "sounds reasonable");
            s = Regex.Replace(s, @"\b[Ss]ounds\s+good\b", "sounds good");
            s = Regex.Replace(s, @"\b[Bb]y\s+the\s+way\b", "incidentally");
            s = Regex.Replace(s, @"\b[Ii]t's\s+up\s+to\s+you\b", "you decide");
            s = Regex.Replace(s, @"\b[Aa]s\s+soon\s+as\s+possible\b", "immediately");
            s = Regex.Replace(s, @"\b[Cc]heck\s+this\s+out\b", "look at this");
            s = Regex.Replace(s, @"\b[Cc]heck\s+it\s+out\b", "look at this");
            s = Regex.Replace(s, @"\b[Ff]or\s+real\b", "seriously");
            s = Regex.Replace(s, @"\b[Cc]alm\s+down\b", "stay calm");
            s = Regex.Replace(s, @"\b[Cc]ome\s+on\b", "hurry up");
            s = Regex.Replace(s, @"\bI\s+have\s+no\s+idea\b", "I do not know at all");
            s = Regex.Replace(s, @"\bI\s+don't\s+have\s+a\s+clue\b", "I do not know at all");
            s = Regex.Replace(s, @"\b[Ll]ong\s+time\s+no\s+see\b", "long time no see");
            s = Regex.Replace(s, @"\b[Ll]et\s+me\s+see\b", "let me check");
            s = Regex.Replace(s, @"\b[Oo]ut\s+of\s+the\s+way\b", "move aside");
            s = Regex.Replace(s, @"\b[Ss]tep\s+aside\b", "move aside");
            s = Regex.Replace(s, @"\b[Dd]on't\s+give\s+up\b", "do not give up");

            // Additional gaming dialogue expressions
            s = Regex.Replace(s, @"\b[Ll]ook\s+who('s|\s+is)\s+here\b", "look who is arriving");
            s = Regex.Replace(s, @"\b[Mm]ake\s+yourself\s+at\s+home\b", "feel comfortable");
            s = Regex.Replace(s, @"\b[Ss]tep\s+on\s+it\b", "hurry up");
            s = Regex.Replace(s, @"\b[Kk]eep\s+your\s+chin\s+up\b", "stay strong");
            s = Regex.Replace(s, @"\b[Cc]ount\s+me\s+in\b", "I will join");
            s = Regex.Replace(s, @"\b[Cc]ount\s+me\s+out\b", "I will not join");
            s = Regex.Replace(s, @"\b(?:I'm\s+)?on\s+my\s+way\b", "I am coming soon");
            s = Regex.Replace(s, @"\b[Tt]ake\s+a\s+look\b", "look at this");
            s = Regex.Replace(s, @"\b[Tt]ake\s+your\s+time\b", "do not rush");
            s = Regex.Replace(s, @"\b[Cc]all\s+it\s+a\s+day\b", "finish for today");
            s = Regex.Replace(s, @"\b[Gg]et\s+a\s+grip\b", "calm yourself down");
            s = Regex.Replace(s, @"\b[Oo]ut\s+of\s+the\s+blue\b", "suddenly");
            s = Regex.Replace(s, @"\b[Hh]ands\s+off\b", "do not touch");
            s = Regex.Replace(s, @"\b[Dd]on't\s+you\s+dare\b", "do not dare");
            s = Regex.Replace(s, @"\b[Hh]ang\s+tight\b", "wait a minute");
            s = Regex.Replace(s, @"\b[Ss]uit\s+yourself\b", "it is your choice");
            s = Regex.Replace(s, @"\b[Mm]y\s+bad\b", "my mistake");
            s = Regex.Replace(s, @"\b[Nn]o\s+big\s+deal\b", "not a big problem");
            s = Regex.Replace(s, @"\b[Nn]o\s+biggie\b", "not a big problem");
            s = Regex.Replace(s, @"\b[Ss]o\s+far\s+so\s+good\b", "everything is fine so far");
            s = Regex.Replace(s, @"\b[Yy]ou\s+nailed\s+it\b", "you did great");
            s = Regex.Replace(s, @"\b[Ww]ay\s+to\s+go\b", "great job");
            s = Regex.Replace(s, @"\b[Ll]et\s+me\s+know\b", "tell me later");
            s = Regex.Replace(s, @"\b[Kk]eep\s+in\s+touch\b", "stay connected");
            s = Regex.Replace(s, @"\bI'm\s+exhausted\b", "I am very tired");
            s = Regex.Replace(s, @"\bI'm\s+starving\b", "I am very hungry");
            s = Regex.Replace(s, @"\b[Nn]ot\s+at\s+all\b", "not at all");
            s = Regex.Replace(s, @"\b[Bb]y\s+all\s+means\b", "please proceed");
            s = Regex.Replace(s, @"\b[Aa]re\s+you\s+(?:crazy|insane|out\s+of\s+your\s+mind)\b", "are you crazy");
            s = Regex.Replace(s, @"\b[Ww]hat\s+on\s+earth\b|\b[Ww]hat\s+the\s+heck\b", "what is happening");
            s = Regex.Replace(s, @"\b[Gg]et\s+out\s+of\s+my\s+way\b", "move away");
            s = Regex.Replace(s, @"\b[Ll]eave\s+me\s+alone\b", "do not disturb me");
            s = Regex.Replace(s, @"\b[Ww]atch\s+your\s+back\b", "be careful");
            s = Regex.Replace(s, @"\b[Dd]on't\s+let\s+your\s+guard\s+down\b", "stay alert");
            s = Regex.Replace(s, @"\b[Ii]t's\s+about\s+time\b", "finally the time has come");
            s = Regex.Replace(s, @"\b[Bb]etter\s+late\s+than\s+never\b", "better late than never");
            s = Regex.Replace(s, @"\b[Ii]n\s+the\s+nick\s+of\s+time\b", "just in time");
            s = Regex.Replace(s, @"\b[Pp]ull\s+yourself\s+together\b", "calm down");
            s = Regex.Replace(s, @"\bI\s+owe\s+you\s+one\b", "I am grateful to you");
            s = Regex.Replace(s, @"\b[Yy]ou\s+can\s+say\s+that\s+again\b", "I completely agree");
            s = Regex.Replace(s, @"\b[Aa]nd\s+here\s+it\s+goes\b", "And it is like this");
            s = Regex.Replace(s, @"\b[Hh]ere\s+it\s+goes\b", "Here we go");
            s = Regex.Replace(s, @"\b[Hh]ere\s+we\s+go\b", "let us begin");

            // Gaming entities & sacred titles (Zelda, Elden Ring, RPGs)
            s = Regex.Replace(s, @"\b[Tt]he\s+Great\s+Deku\s+Tree\b", "The Great Deku Tree");
            s = Regex.Replace(s, @"\bGreat\s+Deku\s+Tree\b", "Great Deku Tree");
            s = Regex.Replace(s, @"\bDeku\s+Nuts?\b", "Deku Nut");
            s = Regex.Replace(s, @"\bDeku\s+Sticks?\b", "Deku Stick");
            s = Regex.Replace(s, @"\bDeku\s+Scrubs?\b", "Deku Scrub");
            s = Regex.Replace(s, @"\bDeku\s+Shield\b", "Deku Shield");
            s = Regex.Replace(s, @"\bDeku\s+Baba\b", "Deku Baba");
            s = Regex.Replace(s, @"\bDeku\s+Sprout\b", "Deku Sprout");
            s = Regex.Replace(s, @"\bDeku\s+Mask\b", "Deku Mask");
            s = Regex.Replace(s, @"\bMaster\s+Sword\b", "Master Sword");
            s = Regex.Replace(s, @"\bKokiri\s+Sword\b", "Kokiri Sword");
            s = Regex.Replace(s, @"\bHylian\s+Shield\b", "Hylian Shield");
            s = Regex.Replace(s, @"\bMirror\s+Shield\b", "Mirror Shield");
            s = Regex.Replace(s, @"\bGreat\s+Fairy\b", "Great Fairy");
            s = Regex.Replace(s, @"\bGreat\s+Plateau\b", "Great Plateau");
            s = Regex.Replace(s, @"\bOcarina\s+of\s+Time\b", "Ocarina of Time");
            s = Regex.Replace(s, @"\bKokiri(?:'s)?\s+Emerald\b", "Kokiri's Emerald");
            s = Regex.Replace(s, @"\bGoron(?:'s)?\s+Ruby\b", "Goron's Ruby");
            s = Regex.Replace(s, @"\bZora(?:'s)?\s+Sapphire\b", "Zora's Sapphire");
            s = Regex.Replace(s, @"\bHyrule\s+Castle\b", "Hyrule Castle");
            s = Regex.Replace(s, @"\bTemple\s+of\s+Time\b", "Temple of Time");
            s = Regex.Replace(s, @"\bDeath\s+Mountain\b", "Death Mountain");
            s = Regex.Replace(s, @"\bKakariko\s+Village\b", "Kakariko Village");
            s = Regex.Replace(s, @"\bLost\s+Woods\b", "Lost Woods");
            s = Regex.Replace(s, @"\bLon\s+Lon\s+Ranch\b", "Lon Lon Ranch");
            s = Regex.Replace(s, @"\bTriforce\b", "Triforce");
            s = Regex.Replace(s, @"\bPrincess\s+Zelda\b", "Putri Zelda");

            return s;
        }

        public static string NaturalizeIndonesian(string translated, string tgtLang, int toneMode)
        {
            // toneMode: 0 = Gaul (Gue/Lo), 1 = Santai (Aku/Kamu), 2 = Baku/Formal
            if (toneMode == 2 || tgtLang != "id" || string.IsNullOrEmpty(translated)) return translated;
            string s = NaturalizeIndonesian(translated, tgtLang, true);
            if (toneMode == 0) s = ApplyIndonesianGaul(s);
            return s;
        }

        public static string ApplyIndonesianGaul(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;

            // 1. Possessive suffixes & prepositional pronouns -> natural Gaul ("lagu gue", "nama lo", "bareng gue", "buat lo")
            s = Regex.Replace(s, @"\b(lagu|nama|teman|temen|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)ku\b", "$1 gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(lagu|nama|teman|temen|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)mu\b", "$1 lo", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\bmilikku\b|\bpunyaku\b", "punya gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmilikmu\b|\bpunyamu\b", "punya lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdiriku\b", "diri gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdirimu\b", "diri lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdenganku\b", "bareng gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdenganmu\b", "bareng lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpadaku\b|\bkepadaku\b", "ke gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpadamu\b|\bkepadamu\b", "ke lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntukku\b", "buat gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntukmu\b", "buat lo", RegexOptions.IgnoreCase);

            // 2. Core pronouns -> Gue / Lo
            s = Regex.Replace(s, @"\bAndalah\b|\bKamulah\b", "Lo yang");
            s = Regex.Replace(s, @"\bandalah\b|\bkamulah\b", "lo yang");
            s = Regex.Replace(s, @"\b(?:Kamu|Kau|Engkau|Anda)\b", "Lo");
            s = Regex.Replace(s, @"\b(?:kamu|kau|engkau|anda)\b", "lo");
            s = Regex.Replace(s, @"\b(?:Aku|Saya)\b", "Gue");
            s = Regex.Replace(s, @"\b(?:aku|saya)\b", "gue");
            s = Regex.Replace(s, @"\b[Kk]ami\b", "kita");

            // 3. Gaul expressions & natural everyday gamer slang
            s = Regex.Replace(s, @"\bmenanyakan\s+(?:lo|kamu|anda)\b|\bmemanggil(?:mu|\s+(?:lo|kamu))\b", "nyariin lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmemintaku\s+buat\s+menjemput(?:mu|\s+lo)\b", "nyuruh gue jemput lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:gue|aku)\s+udah\s+kembali\b", "gue udah balik", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbukankah\s+gitu\b", "iya kan", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmembuat(?:mu|\s+lo)\b", "bikin lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmengelabuhi(?:ku|\s+gue)\b", "nipu gue", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmelindungi(?:mu|\s+lo)\b", "ngelindungin lo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmembantunya\b", "bantu dia", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:begitu|sangat|amat)\s+(keras|berani|bodoh|berharga|keterlaluan|cepet|cepat|lambat|mudah|sulit|susah|gampang|bagus|indah|kuat|lemah|banyak|sedikit|capek|lelah|lapar|haus|bahaya|dekat|jauh|keren|hebat|panjang|pendek|besar|gede|kecil|panas|dingin|penting|aneh|parah|seru|gila|takut|sedih|senang|marah|lucu|serius)\b", "$1 banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bgak\s+apa-apa\b", "gak papa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bgak\s+tahu\b", "gak tau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbaik-baik\s+aja\b", "aman-aman aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btentu\s+aja\b", "pastilah", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btenang\s+aja\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bada\s+apa\b", "kenapa nih", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegitu\s+ya\b", "oh gitu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkerja\s+bagus\b", "mantap banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkalau\b", "kalo", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntuk\b", "buat", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbelum\b", "belom", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbener-bener\b|\bsungguh\b", "beneran", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bseperti\b", "kayak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmelihat\b", "liat", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btahu\b", "tau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmencari\b", "nyari", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmemakai\b|\bmenggunakan\b", "pake", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmembuat\b", "bikin", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmengambil\b", "ngambil", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmendengar\b", "denger", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberpikir\b", "mikir", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberbicara\b", "ngomong", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberkata\b|\bkatakan\b", "bilang", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bberi\s+tahu\b|\bberitahu\b", "kasih tau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmengerti\b", "ngerti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmenunggu\b", "nunggu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bteman\b", "temen", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buang\b", "duit", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\blelah\b", "capek", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bcepatlah\b", "cepetan deh", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bcepat\b", "cepet", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bharus\b", "mesti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsekali\b", "banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsangat\b", "banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegitu\b", "gitu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegini\b", "gini", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkarena\b", "soalnya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bingin\b|\bhendak\b", "pengen", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsedang\b", "lagi", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bakhirnya\b", "ujung-ujungnya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbanget\s+(keras|berani|bodoh|berharga|keterlaluan|cepet|cepat|lambat|mudah|sulit|susah|gampang|bagus|indah|kuat|lemah|banyak|sedikit|capek|lelah|lapar|haus|bahaya|dekat|jauh|keren|hebat|panjang|pendek|besar|gede|kecil|panas|dingin|penting|aneh|parah|seru|gila|takut|sedih|senang|marah|lucu|serius)\b", "$1 banget", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            s = Regex.Replace(s, @"([\.\!\?]\s+)([a-z])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            if (s.Length > 0 && char.IsLower(s[0]))
                s = char.ToUpperInvariant(s[0]) + s.Substring(1);
            return s;
        }

        public static string NaturalizeIndonesian(string translated, string tgtLang, bool naturalTone)
        {
            if (!naturalTone || tgtLang != "id" || string.IsNullOrEmpty(translated)) return translated;

            string s = translated;

            // 1. Fix literal idiom leftovers (Game Idioms & robotic artifacts)
            s = Regex.Replace(s, @"\b[Pp]ohon\s+[Dd]eku\s+(?:yang\s+)?baik\b", "Pohon Deku Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]ohon\s+[Dd]eku\s+hebat\b", "Pohon Deku Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Kk]acang-[Kk]acang|[Bb]iji-[Bb]iji)\s+[Dd]eku\b", "Deku Nuts", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Kk]acang|[Bb]iji)\s+[Dd]eku\b", "Deku Nut", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]ongkat\s+[Dd]eku\b", "Deku Stick", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]erisa?i\s+[Dd]eku\b", "Deku Shield", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]edang\s+[Kk]okiri\b", "Pedang Kokiri", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Hh]utan\s+[Kk]okiri\b", "Hutan Kokiri", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]edang\s+(?:tuan|guru|ahli)\b", "Master Sword", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]eri\s+(?:yang\s+)?baik\b", "Peri Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Pp]eri\s+hebat\b", "Peri Agung", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Zz]amrud|[Ee]merald)\s+[Kk]okiri\b", "Kokiri's Emerald", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Rr]ubi|[Rr]uby)\s+[Gg]oron\b", "Goron's Ruby", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(?:[Ss]afir|[Ss]apphire)\s+[Zz]ora\b", "Zora's Sapphire", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Kk]astil\s+[Ff]rule\b|\b[Ff]rule\s+Castle\b", "Kastil Hyrule", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Kk]astil\s+[Hh]yrule\b", "Kastil Hyrule", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bKuil\s+Waktu\b", "Temple of Time", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bGunung\s+Kematian\b", "Death Mountain", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bDesa\s+Kakariko\b", "Kakariko Village", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bHutan\s+Hilang\b", "Lost Woods", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bPeternakan\s+Lon\s+Lon\b", "Lon Lon Ranch", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bTriforce\b", "Triforce", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\b[Tt]angkap\s+(?:aku|saya|kami)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]angkap\s+(?:kamu|anda|kalian)\s+nanti\b", "sampai ketemu nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]angkap\s+nanti\b", "ketemu nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]enangkap\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]enangkap\s+(?:kamu|anda)\s+nanti\b", "sampai ketemu nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]engejar\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]engejarku\s+nanti\b", "menyusulku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]engejar\s+nanti\b", "susul nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Mm]enyusul\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Tt]angkap\s+(?:aku|saya)\s+jika\s+(?:kamu|anda)\s+bisa\b", "kejar aku kalau bisa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bikuti\s+dan\s+temui\s+(?:aku|saya)\b", "susul aku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btemui\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmenemui\s+(?:aku|saya)\s+nanti\b", "susul aku nanti", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmenemui\s+(?:kamu|anda)\s+nanti\b", "ketemu nanti", RegexOptions.IgnoreCase);

            s = Regex.Replace(s, @"\b(?:[Ss]aya|[Aa]ku)\s+punya\s+ini\b", "biar aku yang urus");
            s = Regex.Replace(s, @"\b(?:[Ss]aya|[Aa]ku)\s+punya\s+punggungmu\b", "aku jagain kamu");
            s = Regex.Replace(s, @"\b[Ss]aya\s+mengerti\b", "aku paham");
            s = Regex.Replace(s, @"\bsaya\s+mengerti\b", "aku paham");
            s = Regex.Replace(s, @"\b(?:[Ss]aya|[Aa]ku)\s+melihat(?=[\.\,\!\?]|\s*$)", "begitu ya");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+salah\b", "ada apa");
            s = Regex.Replace(s, @"\bapa\s+yang\s+salah\b", "ada apa");
            s = Regex.Replace(s, @"\b[Aa]da\s+apa\s+yang\s+salah\b", "ada apa");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+sedang\s+(?:kamu|anda)\s+lakukan\b", "kamu lagi ngapain");
            s = Regex.Replace(s, @"\bapa\s+yang\s+sedang\s+(?:kamu|anda)\s+lakukan\b", "kamu lagi ngapain");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+sedang\s+terjadi\b", "lagi ada apa ini");
            s = Regex.Replace(s, @"\bapa\s+yang\s+sedang\s+terjadi\b", "lagi ada apa ini");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+(?:kamu|anda)\s+bicarakan\b", "kamu ngomong apa sih");
            s = Regex.Replace(s, @"\b[Aa]pa\s+arti\s+yang\s+(?:kamu|anda)\s+maksud\b", "maksud kamu apa");
            s = Regex.Replace(s, @"\b[Aa]pa\s+yang\s+(?:kamu|anda)\s+maksud\b", "maksud kamu apa");
            s = Regex.Replace(s, @"\b[Ss]epotong\s+kue\b", "gampang banget");
            s = Regex.Replace(s, @"\b[Bb]eri\s+aku\s+istirahat\b", "yang bener aja");
            s = Regex.Replace(s, @"\b[Bb]ertahanlah\s+di\s+sana\b", "bertahanlah");
            s = Regex.Replace(s, @"\b[Bb]erpegang\s+teguh\b", "bertahanlah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+ada\s+cara\b", "gak mungkin");
            s = Regex.Replace(s, @"\b[Tt]inggalkan\s+ini\s+padaku\b", "biar aku yang urus");
            s = Regex.Replace(s, @"\b[Kk]epala\s+ke\s+atas\b", "awas");
            s = Regex.Replace(s, @"\b[Jj]angan\s+khawatir\s+tentang\s+itu\b", "tenang aja");
            s = Regex.Replace(s, @"\b[Jj]angan\s+khawatir\b", "tenang aja");
            s = Regex.Replace(s, @"\b[Jj]angan\s+takut\b", "gak usah takut");
            s = Regex.Replace(s, @"\b[Bb]aik-baik\s+saja\b", "baik-baik aja");

            // 2. Conversational Negations ("tidak" -> "gak" for natural gaming Indonesian)
            s = Regex.Replace(s, @"\b[Tt]idak\s+apa-apa\b|\b[Tt]ak\s+apa-apa\b", "gak apa-apa");
            s = Regex.Replace(s, @"\b[Tt]idak\s+masalah\b", "gak masalah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+bisa\b|\b[Tt]ak\s+bisa\b|\b[Tt]idak\s+dapat\b|\b[Tt]ak\s+dapat\b", "gak bisa");
            s = Regex.Replace(s, @"\b[Tt]idak\s+ada\b|\b[Tt]ak\s+ada\b", "gak ada");
            s = Regex.Replace(s, @"\b[Tt]idak\s+tahu\b|\b[Tt]ak\s+tahu\b", "gak tahu");
            s = Regex.Replace(s, @"\b[Tt]idak\s+mau\b|\b[Tt]ak\s+mau\b", "gak mau");
            s = Regex.Replace(s, @"\b[Tt]idak\s+akan\b|\b[Tt]ak\s+akan\b|\b[Tt]akkan\b", "gak bakal");
            s = Regex.Replace(s, @"\b[Tt]idak\s+boleh\b|\b[Tt]ak\s+boleh\b", "gak boleh");
            s = Regex.Replace(s, @"\b[Tt]idak\s+mungkin\b|\b[Tt]ak\s+mungkin\b", "gak mungkin");
            s = Regex.Replace(s, @"\b[Tt]idak\s+perlu\b|\b[Tt]ak\s+perlu\b|\b[Tt]idak\s+usah\b", "gak usah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+suka\b", "gak suka");
            s = Regex.Replace(s, @"\b[Tt]idak\s+peduli\b", "gak peduli");
            s = Regex.Replace(s, @"\b[Tt]idak\s+pernah\b", "gak pernah");
            s = Regex.Replace(s, @"\b[Tt]idak\s+benar\b", "gak bener");
            s = Regex.Replace(s, @"\b[Tt]idak\s+adil\b", "gak adil");
            s = Regex.Replace(s, @"\b[Tt]idak\s+jelas\b", "gak jelas");
            s = Regex.Replace(s, @"\b[Tt]idak\s+percaya\b", "gak percaya");
            s = Regex.Replace(s, @"\b[Tt]idak\s+sempat\b", "gak keburu");
            s = Regex.Replace(s, @"\b[Tt]idak\s+kenal\b", "gak kenal");
            s = Regex.Replace(s, @"\b[Tt]idak\s+enak\b", "gak enak");
            s = Regex.Replace(s, @"\b[Tt]idak\s+sanggup\b", "gak sanggup");
            s = Regex.Replace(s, @"\b[Tt]idak\s+kelihatan\b", "gak kelihatan");
            s = Regex.Replace(s, @"\b[Tt]idak\s+terlalu\b", "gak terlalu");
            s = Regex.Replace(s, @"\b[Tt]idak\s+sama\b", "gak sama");
            s = Regex.Replace(s, @"\b[Tt]idak\s+begitu\b", "gak begitu");
            s = Regex.Replace(s, @"\b[Tt]idak!\b", "Gak!");
            s = Regex.Replace(s, @"\b[Tt]idak,\b", "Gak,");
            s = Regex.Replace(s, @"\btidak\b", "gak");

            // 3. Pronouns, Possessive Suffixes (-ku / -mu) & Conversational Polish (eliminating stiff "lagu saya" / "lagu aku" / "dengan aku")
            s = Regex.Replace(s, @"\b(lagu|nama|teman|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|masa\s+lalu|masa\s+depan|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)\s+(?:saya|aku)\b", "$1ku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b(lagu|nama|teman|sahabat|rumah|tempat|kamar|desa|kota|dunia|tubuh|badan|nyawa|hidup|waktu|kekuatan|kemampuan|pedang|perisai|senjata|tugas|janji|mimpi|impian|harapan|hati|pikiran|perasaan|kepala|mata|tangan|kaki|suara|ayah|ibu|kakak|adik|anak|keluarga|guru|tuan|musuh|kisah|cerita|perjalanan|takdir|nasib|giliran|kesempatan|pilihan|keputusan|kesalahan|rahasia)\s+(?:Anda|kamu)\b", "$1mu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmilik\s+(?:saya|aku)\b", "milikku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmilik\s+(?:Anda|kamu)\b", "milikmu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdiri\s+(?:saya|aku)\b", "diriku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdiri\s+(?:Anda|kamu)\b", "dirimu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdengan\s+(?:saya|aku)\b", "denganku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkepada\s+(?:saya|aku)\b|\bpada\s+(?:saya|aku)\b", "padaku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntuk\s+(?:saya|aku)\b", "untukku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdengan\s+(?:Anda|kamu)\b", "denganmu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkepada\s+(?:Anda|kamu)\b|\bpada\s+(?:Anda|kamu)\b", "padamu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\buntuk\s+(?:Anda|kamu)\b", "untukmu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\b[Dd]an\s+itu\s+berjalan\s+(?:seperti|kayak)\s+(?:ini|gini)", "Dan seperti ini");
            s = Regex.Replace(s, @"\b[Dd]an\s+begini\.\.\.", "Dan seperti ini...");
            s = Regex.Replace(s, @"\bAnda\b", "kamu");
            s = Regex.Replace(s, @"\banda\b", "kamu");
            s = Regex.Replace(s, @"\bSaya\b", "aku");
            s = Regex.Replace(s, @"\bsaya\b", "aku");
            s = Regex.Replace(s, @"\bApakah\b", "apa");
            s = Regex.Replace(s, @"\bapakah\b", "apa");
            s = Regex.Replace(s, @"\bMengapa\b", "kenapa");
            s = Regex.Replace(s, @"\bmengapa\b", "kenapa");
            s = Regex.Replace(s, @"\bBagaimana\b", "gimana");
            s = Regex.Replace(s, @"\bbagaimana\b", "gimana");
            s = Regex.Replace(s, @"\bSiapakah\b", "siapa");
            s = Regex.Replace(s, @"\bsiapakah\b", "siapa");
            s = Regex.Replace(s, @"\bDimanakah\b", "di mana");
            s = Regex.Replace(s, @"\bdimanakah\b", "di mana");
            s = Regex.Replace(s, @"\bKapankah\b", "kapan");
            s = Regex.Replace(s, @"\bkapankah\b", "kapan");
            s = Regex.Replace(s, @"\bBisakah\s+Anda\b", "bisa tolong", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bBisakah\b", "bisa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bDapatkah\b", "bisa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bjika\b", "kalau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bapabila\b", "kalau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bagar\b", "biar", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsupaya\b", "biar", RegexOptions.IgnoreCase);

            // 4. Everyday Game Dialogue Vocabulary & Particles
            s = Regex.Replace(s, @"\b[Ss]aja\b", "aja");
            s = Regex.Replace(s, @"\b[Hh]anya\b", "cuma");
            s = Regex.Replace(s, @"\b[Ss]udah\b", "udah");
            s = Regex.Replace(s, @"\b[Tt]elah\b", "udah");
            s = Regex.Replace(s, @"\b[Tt]erlambat\b", "telat");
            s = Regex.Replace(s, @"\b[Bb]enar-benar\b", "bener-bener");
            s = Regex.Replace(s, @"\byang\s+benar\s+saja\b", "yang bener aja");
            s = Regex.Replace(s, @"\b[Ss]aat\s+ini\b", "sekarang");
            s = Regex.Replace(s, @"\b[Ss]ekarang\s+juga\b", "sekarang juga");
            s = Regex.Replace(s, @"\b[Bb]ergegaslah\b", "cepatlah");
            s = Regex.Replace(s, @"\b[Bb]ergegas\b", "cepat");
            s = Regex.Replace(s, @"\b[Mm]ari\b", "ayo");
            s = Regex.Replace(s, @"\bAkan\s+tetapi\b|\bakan\s+tetapi\b|\bTetapi\b|\btetapi\b|\bNamun\b|\bnamun\b", "tapi");
            s = Regex.Replace(s, @"\bOleh\s+karena\s+itu\b|\boleh\s+karena\s+itu\b", "jadi");
            s = Regex.Replace(s, @"\b[Tt]erima\s+kasih\s+banyak\b", "makasih banyak");
            s = Regex.Replace(s, @"\b[Tt]erima\s+kasih\b", "makasih");
            s = Regex.Replace(s, @"\bseperti\s+ini\b", "kayak gini");
            s = Regex.Replace(s, @"\bseperti\s+itu\b", "kayak gitu");
            s = Regex.Replace(s, @"\bseperti\s+apa\b", "kayak apa");
            s = Regex.Replace(s, @"\bbagaimana\s+jika\b|\bbagaimana\s+kalau\b", "gimana kalau");

            // Naturalize ongoing actions (sedang [verb] -> lagi [verb])
            s = Regex.Replace(s, @"\b[Ss]edang\s+(makan|tidur|bicara|ngomong|nunggu|menunggu|mencari|nyari|pergi|jalan|berjalan|berlari|lari|bertarung|bertempur|istirahat|belajar|bekerja|berlatih|latihan|sembunyi|bersembunyi|bersiap|menyiapkan|menatap|melihat|memikirkan|berpikir)\b", "lagi $1");

            // Naturalize intense adjectives (sangat [adj] -> [adj] banget)
            s = Regex.Replace(s, @"\b(?:[Ss]angat|[Aa]mat)\s+(cepat|lambat|mudah|sulit|gampang|bagus|indah|kuat|lemah|banyak|sedikit|lelah|capek|lapar|haus|bahaya|dekat|jauh|keren|hebat|panjang|pendek|besar|kecil|panas|dingin)\b", "$1 banget");

            // Additional conversational idioms & expressions
            s = Regex.Replace(s, @"\blihat siapa yang (?:ada di sini|sedang tiba)\b", "lihat siapa yang datang", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\banggap saja rumah sendiri\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbukan masalah besar\b", "bukan masalah", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btiba-tiba saja\b", "tiba-tiba", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btinggalkan aku sendiri\b", "jangan ganggu aku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkamu sudah gila\b", "kamu gila ya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bapakah kamu gila\b", "kamu gila ya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkabari aku\b", "kabari aku ya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkerja yang bagus\b", "kerja bagus", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkamu hebat sekali\b", "kamu hebat banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bkeren sekali\b", "keren banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsangat lapar\b", "lapar banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsangat lelah\b", "capek banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsantai saja\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bjangan terburu-buru\b", "santai aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpelan-pelan saja\b", "pelan-pelan aja", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bseperti biasa\b", "kayak biasa", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bseperti semula\b", "kayak semula", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btepat pada waktunya\b", "pas banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\baku berutang budi padamu\b", "aku berutang padamu", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsaya berutang budi padamu\b", "aku berutang padamu", RegexOptions.IgnoreCase);

            // Universal informal transformations (applies to ALL dialogue & sentences)
            s = Regex.Replace(s, @"\b(?:terlihat|tampak)\s+seperti\b", "kelihatan kayak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\brasanya\s+seperti\b", "rasanya kayak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bhendak\b", "mau", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bsebenarnya\b", "sebenernya", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbegitu\s+(cepat|lambat|mudah|sulit|kuat|lemah|banyak|besar|kecil|indah|hebat|keren)\b", "$1 banget", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bamat\s+sangat\b", "bener-bener", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bpada\s+akhirnya\b", "akhirnya", RegexOptions.IgnoreCase);

            // Fix capitalization after punctuation or start of string
            s = Regex.Replace(s, @"([\.\!\?]\s+)([a-z])", m => m.Groups[1].Value + m.Groups[2].Value.ToUpperInvariant());
            if (s.Length > 0 && char.IsLower(s[0]))
                s = char.ToUpperInvariant(s[0]) + s.Substring(1);

            return s;
        }

        public static Bitmap CaptureRect(Rectangle rc)
        {
            try
            {
                if (rc.Width <= 0 || rc.Height <= 0) return null;
                Bitmap bmp = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(rc.X, rc.Y, 0, 0, new System.Drawing.Size(rc.Width, rc.Height), CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch { return null; }
        }

        private static byte[] ComputeSignature(Bitmap bmp)
        {
            const int sw = 48, sh = 24;
            byte[] sig = new byte[sw * sh];
            using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.DrawImage(bmp, 0, 0, sw, sh);
                }
                BitmapData bd = small.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] raw = new byte[bd.Stride * sh];
                    Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
                    for (int y = 0; y < sh; y++)
                    {
                        int rowOff = y * bd.Stride;
                        for (int x = 0; x < sw; x++)
                        {
                            int off = rowOff + x * 4;
                            sig[y * sw + x] = (byte)((raw[off] * 29 + raw[off + 1] * 150 + raw[off + 2] * 77) >> 8);
                        }
                    }
                }
                finally { small.UnlockBits(bd); }
            }
            return sig;
        }

        private static float ComputeDiffPercent(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length || a.Length == 0) return 100f;
            int changed = 0;
            for (int i = 0; i < a.Length; i++) if (Math.Abs(a[i] - b[i]) > 8) changed++;
            return (changed * 100f) / a.Length;
        }

        private static string MapWinRtLangTag(string appCode)
        {
            switch (appCode)
            {
                case "en": return "en-US";
                case "ja": return "ja-JP";
                case "ko": return "ko-KR";
                case "zh-CN": return "zh-Hans-CN";
                case "zh-TW": return "zh-Hant-TW";
                case "ru": return "ru-RU";
                case "es": return "es-ES";
                case "fr": return "fr-FR";
                case "de": return "de-DE";
                default: return "auto";
            }
        }

        private static string MapOcrSpaceLang(string appCode)
        {
            switch (appCode)
            {
                case "ja": return "jpn";
                case "ko": return "kor";
                case "zh-CN": return "chs";
                case "zh-TW": return "cht";
                case "ru": return "rus";
                case "es": return "spa";
                case "fr": return "fre";
                case "de": return "ger";
                default: return "eng";
            }
        }

        private bool HasLocalPackFor(string appCode)
        {
            if (appCode == "auto" || string.IsNullOrEmpty(appCode)) return _installedOcrLangs.Count > 0;
            string prefix = appCode.Split('-')[0].ToLowerInvariant();
            foreach (string tag in _installedOcrLangs)
                if (tag.ToLowerInvariant().StartsWith(prefix)) return true;
            return false;
        }

        private OcrEngine GetWinRtEngine(string appCode)
        {
            string tag = MapWinRtLangTag(appCode);
            if (_ocrEngine != null && _cachedOcrLangTag == tag) return _ocrEngine;
            OcrEngine eng = null;
            try
            {
                if (tag != "auto")
                {
                    Language lang = new Language(tag);
                    if (OcrEngine.IsLanguageSupported(lang)) eng = OcrEngine.TryCreateFromLanguage(lang);
                }
                if (eng == null) eng = OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { }
            _ocrEngine = eng;
            _cachedOcrLangTag = tag;
            return _ocrEngine;
        }

        private static TR AwaitWinRt<TR>(IAsyncOperation<TR> op)
        {
            if (op.Status == AsyncStatus.Completed) return op.GetResults();
            using (ManualResetEventSlim ev = new ManualResetEventSlim(false))
            {
                try
                {
                    op.Completed = delegate { try { ev.Set(); } catch { } };
                    if (op.Status == AsyncStatus.Started) ev.Wait(2200);
                }
                catch
                {
                    int tries = 0;
                    while (op.Status == AsyncStatus.Started && tries < 2200) { Thread.Sleep(1); tries++; }
                }
            }
            if (op.Status != AsyncStatus.Completed) throw new Exception("WinRT OCR status: " + op.Status);
            return op.GetResults();
        }

        public string RecognizeBitmap(Bitmap sourceBmp, string srcLang, out bool usedOnline, out string error)
        {
            bool dummy;
            return RecognizeBitmap(sourceBmp, srcLang, _cfg.EnhanceGameOcr, out usedOnline, out dummy, out error);
        }

        public string RecognizeBitmap(Bitmap sourceBmp, string srcLang, out bool usedOnline, out bool detectedChoiceLayout, out string error)
        {
            return RecognizeBitmap(sourceBmp, srcLang, _cfg.EnhanceGameOcr, out usedOnline, out detectedChoiceLayout, out error);
        }

        public static int ScoreOcrQuality(string raw, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(raw)) return 0;
            string s = raw.Trim();
            if (s.Length < 2) return 0;

            bool isCjk = (srcLang == "ja" || srcLang == "zh-CN" || srcLang == "zh-TW" || srcLang == "ko");
            bool isEuroLatin = (srcLang == "es" || srcLang == "fr" || srcLang == "de");
            int score = 100;

            int quoteCount = 0;
            int cjkChars = 0;
            foreach (char c in s)
            {
                if (c == '"') quoteCount++;
                if (isCjk && ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x4E00 && c <= 0x9FFF) || (c >= 0xAC00 && c <= 0xD7AF)))
                    cjkChars++;
                if (c == '<' || c == '>' || c == '*' || c == '&' || c == '~' || c == '^' || c == '|' || c == '_' || c == '#' || c == '@' ||
                    c == '%' || c == '$' || c == '+' || c == '=' || c == '\\' || c == '/' || c == '{' || c == '}' || c == '•' || c == '„' || c == 'ß')
                    score -= 20;
                else if (!isCjk && !isEuroLatin && c > 127 && c != '’' && c != '“' && c != '”' && c != '…' && c != '—' && c != '•')
                    score -= 16;
            }
            if (isCjk && cjkChars == 0 && s.Length >= 3) score -= 45;
            if ((quoteCount & 1) == 1) score -= 16; // Penalize unmatched stray double-quote artifacts (e.g. Whenever" wapt%talk)

            if (!isCjk)
            {
                // Penalize quote/bracket/symbol noise clusters & glued-dot 3D artifacts (e.g. "and•the", "prineess.tgc", "promisetONabooru.u", "effi'hTIet's")
                score -= Regex.Matches(s, @"[A-Za-z][""\(\)\[\]\{\}%$+=\\\/•„]+[A-Za-z\(\)\[\]]").Count * 24;
                score -= Regex.Matches(s, @"[a-z]\.[a-z]{1,}").Count * 22;
                score -= Regex.Matches(s, @"[a-z]'[A-Z]|[a-z]{2,}'[a-z]{3,}").Count * 24;
                score -= Regex.Matches(s, @"['""]{2,}|\(\)|\[\]|[:;]{2,}").Count * 18;
                // Penalize weird mid-word uppercase glitches from 3D noise (e.g. "mUst", "trickS", "sneakY", "promisetONabooru", "hTIet", "noOup")
                score -= Regex.Matches(s, @"[a-z][A-Z]{2,}").Count * 26;
                score -= Regex.Matches(s, @"\b[a-z]{2,}[A-Z][a-zA-Z]*\b").Count * 18;
                score -= Regex.Matches(s, @"\b[A-Z][a-z]+[A-Z][a-z]+\b").Count * 14;
                // Penalize broken impossible English word endings caused by 3D noise (e.g. "tricksw", "foolr", "tohancl")
                score -= Regex.Matches(s, @"\b[a-z]{3,}(?:sw|lr|cl|zr|qn|vx|xz|jw|kw|bf|df|gf|pf|tf|vt|zp)\b", RegexOptions.IgnoreCase).Count * 18;
                // Penalize glued mega-words & impossible 3D-noise consonant/bigram glitches (e.g. "kryown", "Icevto", "alononat", "kidzup", "haueltohelp")
                score -= Regex.Matches(s, @"\b[a-zA-Z]{16,}\b").Count * 22;
                score -= Regex.Matches(s, @"(?:kry|vto|zup|dzup|nonat|lwee|coul\b|gu\s+es|wontt)", RegexOptions.IgnoreCase).Count * 20;
                score -= Regex.Matches(s, @"[bcdfghjklmnpqrstvwxz]{5,}", RegexOptions.IgnoreCase).Count * 16;

                string[] words = Regex.Split(s, @"\s+");
                int validWords = 0;
                foreach (string w in words)
                {
                    string trimmedW = w.Trim('.', ',', '!', '?', ';', ':', '"', '\'', '“', '”', '’', '(', ')', '[', ']');
                    if (trimmedW.Length == 0) continue;

                    // Penalize suspicious digit hallucinations (e.g. "11" for "I'm", "1001"/"100k" for "look", "t0" for "to")
                    if (Regex.IsMatch(trimmedW, @"^(?:11|1001|100k|l00k|1ook|0|1)$", RegexOptions.IgnoreCase))
                    {
                        score -= 18;
                        continue;
                    }
                    if (Regex.IsMatch(trimmedW, @"[0-9]") && Regex.IsMatch(trimmedW, @"[A-Za-z]") &&
                        !Regex.IsMatch(trimmedW, @"^(?:\d+(?:st|nd|rd|th|s|g|k|m|p|x|d|hp|mp|xp|lv)|lv\d+)$", RegexOptions.IgnoreCase))
                    {
                        score -= 18;
                    }

                    // Penalize hyphenated single-letter OCR glitches (e.g. "Tree-i") and non-English 2-letter splits (e.g. "De cu")
                    if (Regex.IsMatch(trimmedW, @"^[A-Za-z]+-[A-Za-z]$"))
                        score -= 16;

                    string cleanW = Regex.Replace(trimmedW, @"[^A-Za-z]", "");
                    if (cleanW.Length == 0) continue;
                    if (cleanW.Length == 1 && cleanW != "I" && cleanW != "a" && cleanW != "A")
                        score -= 12;
                    else if (cleanW.Length == 2 && !Regex.IsMatch(cleanW, @"^(?:no|go|ok|oh|ah|hi|up|on|in|it|he|we|so|do|be|me|my|by|or|if|at|as|us|to|an|am|is|of|mr|ms|dr|st|tv|hp|mp|xp)$", RegexOptions.IgnoreCase))
                        score -= 12;
                    else if (cleanW.Length >= 3 && !Regex.IsMatch(cleanW, @"[aeiouyAEIOUY]"))
                        score -= 18;
                    else
                        validWords++;
                }
                if (validWords == 0) return 0;
            }

            return Math.Max(0, score);
        }

        public static bool IsMeaningfulSubtitle(string cleanText, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(cleanText)) return false;
            string s = cleanText.Trim();
            bool isCjk = (srcLang == "ja" || srcLang == "zh-CN" || srcLang == "zh-TW" || srcLang == "ko");
            if (isCjk) return s.Length >= 1;

            // Reject pure punctuation/numbers or 1-character noise on moving video frames
            string lettersOnly = Regex.Replace(s, @"[^A-Za-z]", "");
            if (lettersOnly.Length < 2) return false;

            // Require at least one vowel in English/Latin subtitles
            if (!Regex.IsMatch(lettersOnly, @"[aeiouyAEIOUY]")) return false;

            // Reject random mixed-case nebula/effect hallucinations (e.g. "uOHMA", "cneFG")
            if (Regex.IsMatch(s, @"^[a-z][A-Z]{3,}$")) return false;

            // Reject single-word random gibberish with no punctuation & suspicious consonant clusters (e.g. "Nisciorn")
            if (!s.Contains(" ") && s.Length >= 5 && !EndsWithTerminalPunctuation(s))
            {
                if (Regex.IsMatch(lettersOnly, @"(?:sci|xqz|qxz|zqj|rn$|iorm|iorn)$", RegexOptions.IgnoreCase) &&
                    !Regex.IsMatch(lettersOnly, @"^(?:return|modern|pattern|eastern|western|northern|southern|cavern|lantern|tavern|concern|discern|unicorn|acorn|scorn|thorn|sworn|worn|born|horn|corn|torn|morn|forlorn)$", RegexOptions.IgnoreCase))
                    return false;
            }

            // If very short (2-3 letters total), ensure it's not random symbol noise
            if (lettersOnly.Length <= 2 && s.Length <= 3)
            {
                return Regex.IsMatch(s, @"^(?:no|go|ok|oh|ah|hi|up|on|in|it|he|we|so|do|be|me|my|by|or|if|at|as|us|to)[\.\!\?]?$", RegexOptions.IgnoreCase);
            }
            return true;
        }

        public static string CleanGameOcrText(string text, string srcLang)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            string s = text;

            // 0. Strip leading/trailing 3D-background noise ghosts (e.g. "fib Din,", ") Play this song...", "Hi,} Nabooru!", "saveßyrule")
            s = Regex.Replace(s, @"^(?:fib|flb|fih|tih|ltb|lfb|lhb|iib)\s+(?=[A-Z][a-z]{2,})", "");
            s = Regex.Replace(s, @"^[\)\}\]\•\„\—\s]+(?=[A-Za-z""'“])", "");
            s = Regex.Replace(s, @"(?<=[a-z])ß(?=[a-z])", " H");
            s = s.Replace("ß", "H").Replace("„", " ");
            s = Regex.Replace(s, @"(?<=[A-Za-z])•(?=[A-Za-z])", " ");
            s = Regex.Replace(s, @"[\{\}]", "");

            // 0b. Universal thin-font split-'k' & serif OCR repair (Delcu->Deku, talce->take, malce->make, Guardiar->Guardian, forese->forest)
            s = Regex.Replace(s, @"\bDelcu\b", "Deku", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btalce\b", "take", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bmalce\b", "make", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bwalce\b", "wake", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbaclc\b", "back", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdarlc\b", "dark", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bspealc\b", "speak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbrealc\b", "break", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bwealc\b", "weak", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bloolc\b|\blooIc\b", "look", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bcloclc\b", "clock", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bbloclc\b", "block", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bGuardiar\b", "Guardian", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bforese\b", "forest", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\baland\b", "a land", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btyne\b", "the", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bfighling\b", "fighting", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bhoyal\b", "Royal", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bdidnlt\b", "didn't", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bwontt\b", "won't", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\blhere\b", "there", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bprineess\b", "princess", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bgoddes\b", "goddess", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btohancl\b", "to hand", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bhaueltohelp\b", "have to help", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\boppesing\b", "opposing", RegexOptions.IgnoreCase);

            // 1. Clustered punctuation & noise symbols
            s = Regex.Replace(s, @"[:;]{2,}", ":");
            s = Regex.Replace(s, @"(?:\:|\;)?\.{2,}'?", "...");
            s = Regex.Replace(s, @"\b(tal|wal|fol)<(?!\w)", "$1k", RegexOptions.IgnoreCase); // tal< -> talk, wal< -> walk
            s = Regex.Replace(s, @"l<", "k");                             // mal<e -> make, lil<e -> like
            s = Regex.Replace(s, @"(?<=[A-Za-z])<(?=[A-Za-z])", "k");      // < between letters -> k
            s = Regex.Replace(s, @"(?<=[\s\(\[\{]|^)[<>\*~^_#]+", "");     // leading noise symbols (keep | for I-restoration first)
            s = Regex.Replace(s, @"[<>\*~^|_#]+(?=[\s\)\.\,\!\?\]\}]|$)", ""); // trailing noise symbols
            s = Regex.Replace(s, @"[&*]+([A-Za-z\u00C0-\u024F])", "$1");   // &word -> word
            s = Regex.Replace(s, @"([A-Za-z\u00C0-\u024F])[&*]+", "$1");   // word* -> word
            s = Regex.Replace(s, @"[<>]", " ");                            // strip remaining stray brackets

            // 2. Glued noise digits/letters between words (e.g. power4eGo -> power. Go)
            s = Regex.Replace(s, @"(?<=[a-z]{2,})\d+[a-z]{1,2}(?=[A-Z])", ". ");

            // 3. Glued lowercase-to-uppercase words (e.g. DearNavi -> Dear Navi)
            s = Regex.Replace(s, @"(?<=[a-z])(?=[A-Z][a-z])", " ");

            // 4. Universal English / Latin Digit-to-Letter & Ligature OCR Restoration (Applies to ALL games)
            if (srcLang == "en" || srcLang == "auto")
            {
                // 4a. Contractions & Pronoun "I" misread as "11", "1'm", "l'm", "|'m", "I'rn", "l", "1", "|"
                // Also fix curly/acute/backtick apostrophes (’, `, ´) so all contraction rules match reliably
                s = Regex.Replace(s, @"(?<=[A-Za-z0-9\|!])[’`´](?=[A-Za-z])", "'");
                // Fix OCR spaces around apostrophe in common contractions (e.g. "don 't" -> "don't", "I 'm" -> "I'm", "Saria 's" -> "Saria's")
                s = Regex.Replace(s, @"\b([A-Za-z]+)\s+'\s*(s|t|m|re|ve|ll|d)\b", "$1'$2", RegexOptions.IgnoreCase);

                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'m\b", "I'm");
                s = Regex.Replace(s, @"\b(?:1m|I'rn|Irn|11m|11'm)\b", "I'm");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'ll\b", "I'll");
                s = Regex.Replace(s, @"\b(?:1ll|11ll|I1l|Il1)\b", "I'll");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'ve\b", "I've");
                s = Regex.Replace(s, @"\b1ve\b", "I've");
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|!1l])'d\b", "I'd");
                s = Regex.Replace(s, @"\b([Yy]ou|[Ww]e|[Tt]hey)'11\b", "$1'll");

                // Fix common missing-apostrophe contractions from low-contrast fonts (avoiding real words like "ill", "well", "hell", "shell", "cant", "wont" handled safely)
                s = Regex.Replace(s, @"\bdont\b", "don't");
                s = Regex.Replace(s, @"\bDont\b", "Don't");
                s = Regex.Replace(s, @"\bdidnt\b", "didn't");
                s = Regex.Replace(s, @"\bDidnt\b", "Didn't");
                s = Regex.Replace(s, @"\bdoesnt\b", "doesn't");
                s = Regex.Replace(s, @"\bDoesnt\b", "Doesn't");
                s = Regex.Replace(s, @"\bisnt\b", "isn't");
                s = Regex.Replace(s, @"\bIsnt\b", "Isn't");
                s = Regex.Replace(s, @"\barent\b", "aren't");
                s = Regex.Replace(s, @"\bArent\b", "Aren't");
                s = Regex.Replace(s, @"\bwasnt\b", "wasn't");
                s = Regex.Replace(s, @"\bWasnt\b", "Wasn't");
                s = Regex.Replace(s, @"\bwerent\b", "weren't");
                s = Regex.Replace(s, @"\bWerent\b", "Weren't");
                s = Regex.Replace(s, @"\bwouldnt\b", "wouldn't");
                s = Regex.Replace(s, @"\bWouldnt\b", "Wouldn't");
                s = Regex.Replace(s, @"\bcouldnt\b", "couldn't");
                s = Regex.Replace(s, @"\bCouldnt\b", "Couldn't");
                s = Regex.Replace(s, @"\bshouldnt\b", "shouldn't");
                s = Regex.Replace(s, @"\bShouldnt\b", "Shouldn't");
                s = Regex.Replace(s, @"\bhavent\b", "haven't");
                s = Regex.Replace(s, @"\bHavent\b", "Haven't");
                s = Regex.Replace(s, @"\bhasnt\b", "hasn't");
                s = Regex.Replace(s, @"\bHasnt\b", "Hasn't");
                s = Regex.Replace(s, @"\bhadnt\b", "hadn't");
                s = Regex.Replace(s, @"\bHadnt\b", "Hadn't");
                s = Regex.Replace(s, @"\bwont\b", "won't");
                s = Regex.Replace(s, @"\bWont\b", "Won't");
                s = Regex.Replace(s, @"\bcant\b", "can't");
                s = Regex.Replace(s, @"\bCant\b", "Can't");
                s = Regex.Replace(s, @"\bIm\b", "I'm");
                s = Regex.Replace(s, @"\bIve\b", "I've");
                s = Regex.Replace(s, @"\byoure\b", "you're");
                s = Regex.Replace(s, @"\bYoure\b", "You're");
                s = Regex.Replace(s, @"\btheyre\b", "they're");
                s = Regex.Replace(s, @"\bTheyre\b", "They're");
                s = Regex.Replace(s, @"\bthats\b", "that's");
                s = Regex.Replace(s, @"\bThats\b", "That's");
                s = Regex.Replace(s, @"\bwhats\b", "what's");
                s = Regex.Replace(s, @"\bWhats\b", "What's");
                s = Regex.Replace(s, @"\bheres\b", "here's");
                s = Regex.Replace(s, @"\bHeres\b", "Here's");
                s = Regex.Replace(s, @"\btheres\b", "there's");
                s = Regex.Replace(s, @"\bTheres\b", "There's");

                // "11 <base verb>" -> "I'll <verb>"
                s = Regex.Replace(s, @"\b11\s+(?=(?:go|do|be|take|make|see|tell|give|get|find|help|wait|show|protect|handle|leave|never|have|try|come|bring|keep|let|stop|start|need|check|ask|meet|catch|kill|destroy|save|fix|open|close|use|buy|pay|stay|return|follow)\b)", "I'll ");

                // "11 <non-unit word>" at start of sentence/clause or after conjunction -> "I'm "
                s = Regex.Replace(s, @"(?:(?<=^|[\.\!\?\,\;\:""'“”\(]\s*)|(?<=\b(?:and|but|so|because|since|that|if|when|while|though|although)\s+))11\s+(?!(?:years|days|hours|minutes|seconds|months|weeks|times|people|men|women|children|items|coins|gold|bucks|dollars|yen|gil|rupees|souls|points|percent|o'clock|am|pm|levels|floors|steps|miles|meters|feet|inches|pounds|kg|cm|mm|of\b)\b)(?=[A-Za-z])", "I'm ");

                // "11 <present participle / state adjective>" anywhere -> "I'm "
                s = Regex.Replace(s, @"\b11\s+(?=(?:[a-z]+ing|sorry|sure|ready|glad|afraid|happy|tired|exhausted|fine|okay|alright|here|back|not|just|still|only|already|so|too|very|quite|really|about|on|in|at|from|with|a|an|the|your|her|his|my|our|their|done|finished|dead|alive|hurt|stuck|lost|late|certain|positive|worried|scared|proud|honored|grateful|thankful)\b)", "I'm ");

                // Standalone l / 1 / | before common verbs -> "I "
                s = Regex.Replace(s, @"(?:(?<=^|[\s\(\[""'“”])[\|1l])\s+(?=(?:am|was|were|have|had|will|would|can|could|should|shall|must|may|might|need|want|think|thought|know|knew|see|saw|feel|felt|hope|wish|guess|suppose|believe|understand|remember|forgot|mean|meant|said|told|asked|heard|found|got|took|made|came|went|left|gave|tried|used|loved|hated|feared|doubted|wonder|bet|swear|promise|refuse|agree|disagree|don't|didn't|won't|can't|couldn't|shouldn't|wouldn't|haven't|hadn't|wasn't|cannot)\b)", "I ");

                // 4b. "1001" / "100k" / "l00k" / "1ook" -> "Look" (at sentence start) or "look"
                s = Regex.Replace(s, @"(?<=^|[\.\!\?""'“”]\s*)(?:1001|100k|l00k|L00k|L001|100K)(s|ed|ing)?\b(?!(?:\s+(?:nights|arabian|dalmatians|years|coins|points|gold|rupees|gil|exp|hp|mp))\b)", "Look$1");
                s = Regex.Replace(s, @"(?<!\b(?:year|room|page|level|lvl|id|code|error|port)\s+)\b(?:1001|100k|l00k|1ook|l001|10ok|lo0k)(s|ed|ing)?\b(?!(?:\s+(?:nights|arabian|dalmatians|years|coins|points|gold|rupees|gil|exp|hp|mp))\b)", "look$1");
                s = Regex.Replace(s, @"\b(?:L00k|L001|100K)(s|ed|ing)?\b", "Look$1");

                // 4c. "10-" words -> "lo-" (10st -> lost, 10ng -> long, 10ve -> love, 10rd -> lord, 10ck -> lock, 10w -> low, 10ud -> loud, 100se -> loose)
                s = Regex.Replace(s, @"\b10(st|ng|ve[dsr]?|rd[s]?|ck(?:s|ed|ing)?|w(?:er|est|ly)?|ud(?:er|est|ly)?|0se|cal|nely|cat(?:e|ed|ing|ion))\b", "lo$1");

                // 4d. Leading '1' before letters -> 'I' or 'l' (excluding 1st)
                s = Regex.Replace(s, @"\b1(t's|t|n|s|f|nto|nside|ndeed|nstead|mpossible|mportant|mmortal|sland|tem|dea|diot|gnore|llusion|magine|nform|nvite|nclude|ncrease|nsist)\b", "I$1");
                s = Regex.Replace(s, @"\b1(?!st\b)([a-z]{2,})\b", "l$1");

                // 4e. Internal/trailing '1' adjacent to letters -> 'l' (wi11 -> will, te11 -> tell, care1ess1y -> carelessly)
                s = Regex.Replace(s, @"(?<=[a-zA-Z])1+", delegate(Match m) { return new string('l', m.Length); });

                // 4f. Digit '0' adjacent to letters -> 'o' / 'O' (t0 -> to, g0 -> go, y0u -> you, w0rld -> world, 0ne -> one)
                s = Regex.Replace(s, @"\b0([a-z]+)\b", "o$1");
                s = Regex.Replace(s, @"\b0([A-Z][a-z]+)\b", "O$1");
                s = Regex.Replace(s, @"(?<=[a-zA-Z])0+", delegate(Match m) { return new string('o', m.Length); });

                // 4g. Common Font Ligatures & Small-Font Misreads: vv -> w, rn -> m, % -> to, " -> you
                s = Regex.Replace(s, @"\b([Ww]henever|[Ii]f|[Ww]hen|[Ww]herever|[Ww]hatever)\s*[""“”']+\s*(?=(?:wapt|wanr|vvant|waht|want|need|like|can|could|are|were|have|had|see|go|come|get|find|hear|feel|think|know|play|talk|call|speak|ask|tell|try|use|make|take|give|wish|choose|decide)\b)", "$1 you ");
                s = Regex.Replace(s, @"\b(?:wapt|wanr|vvant|waht)\b", "want");
                s = Regex.Replace(s, @"\b(?:Wapt|Wanr|VVant|Waht)\b", "Want");
                // In small fonts, "to" (t + o) is frequently misread as "%" (o/o) between words (e.g. "wapt%talk" -> "want to talk")
                s = Regex.Replace(s, @"(?<=\b(?:want|wants|wanted|going|try|trying|tried|need|needs|needed|have|has|had|used|ready|able|unable|time|way|how|what|where|who|nothing|something|anything|someone|anyone|place|like|likes|liked|love|loves|loved|start|started|begin|began|continue|continued|decide|decided|promise|promised|refuse|refused|forget|forgot|remember|remembered|learn|learned|teach|taught|help|helped|come|came|go|went|listen|talk|speak|say|tell|ask|call|play|move|return|get|got|make|made))\s*%\s*(?=[A-Za-z])", " to ", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"(?<=[A-Za-z])\s*%\s*(?=(?:talk|speak|say|tell|ask|see|look|hear|listen|go|come|get|make|take|give|find|know|think|feel|play|call|use|try|help|save|protect|fight|defeat|open|close|enter|leave|return|stay|live|die|be|do|have|meet|catch|follow|run|walk|stop|start|begin|finish|end|win|lose|buy|sell|pay|eat|drink|sleep|wake|read|write|learn|understand|believe|remember|forget|me|you|him|her|us|them|it|this|that|here|there|the|a|an|my|your|his|our|their)\b)", " to ", RegexOptions.IgnoreCase);
                s = Regex.Replace(s, @"(?<=[A-Za-z])%(?=[A-Za-z])", " to ");

                // Strip unmatched stray single double-quote '"' attached to a word in the middle of a sentence
                int qCnt = 0;
                foreach (char qc in s) if (qc == '"') qCnt++;
                if ((qCnt & 1) == 1)
                {
                    s = Regex.Replace(s, @"(?<=[A-Za-z])""(?=\s+[a-z])", "");
                }

                s = Regex.Replace(s, @"\bvv([a-zA-Z]+)\b", "w$1");
                s = Regex.Replace(s, @"\bVV([a-zA-Z]+)\b", "W$1");
                s = Regex.Replace(s, @"\brn(ust|ake|ade|any|ore|ost|uch|y|e|ean|eant|ight|ind|agic|onster|aster|other|ove|oment|ountain|ission|ay|an|en)\b", "m$1");
                s = Regex.Replace(s, @"\b(ti|so|co|ho|na|ga|sa|fla|fra|sha|bla)rne(s|d|one|thing|where|how|times)?\b", "$1me$2");
                s = Regex.Replace(s, @"\bfrorn\b", "from");
                s = Regex.Replace(s, @"\btherne\b", "theme");
                s = Regex.Replace(s, @"\benern(y|ies)\b", "enem$1");
                s = Regex.Replace(s, @"\b(?:rernember|remernber)\b", "remember");
            }

            // 5. Classic Retro / Adventure Font Typo Corrections
            s = Regex.Replace(s, @"\bLinl[ec]\b|\bLineha\b", "Link");
            s = Regex.Replace(s, @"\bfrule\s+Castle\b", "Hyrule Castle");
            s = Regex.Replace(s, @"\bfrule\b", "Hyrule");
            s = Regex.Replace(s, @"\bEar\s+Navi\b", "Dear Navi");
            s = Regex.Replace(s, @"\bOwevfr\b", "However");
            s = Regex.Replace(s, @"\bMusteprotect\b", "Must protect");
            s = Regex.Replace(s, @"\bIfiite:e\b|\bIfiite\b", "if it");
            s = Regex.Replace(s, @"\bChe;\b|\bChe\b(?=\s+[a-z]{3,})", "the");
            s = Regex.Replace(s, @"\berad\b(?=\s+of)", "end");
            s = Regex.Replace(s, @"\bT0\*\b|\bT0\b", "To");
            s = Regex.Replace(s, @"\bIbhppe\s+o\b|\bIbhppe\b", "beyond help");
            s = Regex.Replace(s, @"\binteétioh\b|\binteetioh\b", "intention");

            // 6. Filter genuine non-ASCII / mixed-case 3D texture garbage tokens (e.g. 9äUxyfios) without deleting valid words or numbers
            if (srcLang == "en" || srcLang == "auto")
            {
                string[] tokens = s.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                List<string> cleanTokens = new List<string>();
                foreach (string tok in tokens)
                {
                    string t = tok.Trim();
                    if (t.Length == 0) continue;

                    // Only drop token if it starts with a digit AND has non-ASCII diacritics or random mid-word uppercase (like 9äUxyfios)
                    if (Regex.IsMatch(t, @"^\d+[a-zA-Z\u00C0-\u024F]*[\u00C0-\u024F][a-zA-Z\u00C0-\u024F]*$") ||
                        Regex.IsMatch(t, @"^\d+[a-z]+[A-Z][a-zA-Z0-9]{2,}$"))
                        continue;

                    // Isolated noise punctuation
                    if (Regex.IsMatch(t, @"^[•*#~^\|\\/<>_]{1,3}$"))
                        continue;

                    cleanTokens.Add(t);
                }
                s = string.Join(" ", cleanTokens.ToArray());

                // 7. Universal Diacritic Normalization for English/Latin text
                s = RemoveDiacritics(s);
                // Strip single stray consonants at sentence boundaries (e.g. "protect t.")
                s = Regex.Replace(s, @"\b([a-zA-Z]{3,})\s+[b-df-hj-np-tv-zB-DF-HJ-NP-TV-Z](?=[\.\,\!\?]|\s*$)", "$1");
            }

            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        public static string RemoveDiacritics(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                string normalized = text.Normalize(NormalizationForm.FormD);
                StringBuilder sb = new StringBuilder();
                foreach (char c in normalized)
                {
                    var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
                    if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                        sb.Append(c);
                }
                return sb.ToString().Normalize(NormalizationForm.FormC);
            }
            catch { return text; }
        }

    }
}
