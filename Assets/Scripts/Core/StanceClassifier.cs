using ScamSim.Logging;

namespace ScamSim.Core
{
    /// <summary>
    /// Keyword heuristic that sorts a free-text reply into comply / hesitate / resist.
    /// It is deliberately simple and transparent: every free-text turn is logged with
    /// player_stance_source = "heuristic" so analysts can re-code the raw text later.
    /// Phrases are checked longest-meaning first and removed once counted, so "ไม่โอน" is a
    /// refusal and never also counts as the comply word "โอน".
    /// </summary>
    public static class StanceClassifier
    {
        const string Not = "ไม่";

        // Agreeing phrases that contain a word the later lists would read as refusal or as a question.
        static readonly string[] AgreePhrases =
        {
            "ไม่มีปัญหา", "อะไรก็ได้", "อะไรก็ยอม"
        };

        // "ไม่…" phrases that say nothing about whether the player goes along.
        static readonly string[] NeutralPhrases =
        {
            "ไม่เป็นไร", "ไม่ต้องห่วง", "ไม่ต้องกังวล", "ไม่เคย"
        };

        static readonly string[] HesitatePhrases =
        {
            "ไม่แน่ใจ", "ไม่รู้", "ไม่ได้มี", "ไม่มีเงิน", "ไม่มีตังค์", "ไม่พอ",
            "ขอคิด", "ขอเวลา", "ขอปรึกษา", "เดี๋ยวก่อน", "รอก่อน", "ใช่ไหม", "ใช่มั้ย", "เอ่อ", "กลัว", "งง"
        };

        static readonly string[] ResistPhrases =
        {
            "ไม่ให้", "ไม่บอก", "ไม่เชื่อ", "ไม่กด", "ไม่ทำ", "ไม่โหลด", "ไม่ติดตั้ง", "ไม่ส่ง",
            "วางสาย", "แจ้งความ", "โทรกลับ", "1441", "หลอก", "มิจ", "ปลอม", "โกง", "ตำรวจจริง",
            "หมายเรียก", "ไปที่สถานี", "ไปสถานี", "สถานีตำรวจ", "โรงพัก", "ถามธนาคาร", "โทรหาธนาคาร",
            "ขอตรวจสอบ"
        };

        static readonly string[] QuestionWords =
        {
            "ทำไม", "อะไร", "ยังไง", "อย่างไร", "ใคร", "ที่ไหน", "เหรอ", "หรอ", "ไหม", "มั้ย", "หรือเปล่า", "?"
        };

        static readonly string[] ComplyPhrases =
        {
            "ได้", "ตกลง", "โอเค", "ยอม", "โอน", "ส่งมา", "บอกมา", "เลขบัตร", "กดแล้ว", "ติดตั้งแล้ว", "โหลดแล้ว",
            "ครับผม", "รับทราบ", "ช่วยด้วย", "ทำตาม", "ให้ความร่วมมือ", "ยินดี", "พร้อม", "ok", "yes"
        };

        /// <param name="maskedText">The reply after PIIMasker.Mask.</param>
        /// <param name="gaveIdNumber">True when the masker removed a 13-digit ID number from the reply.</param>
        public static Stance Classify(string maskedText, bool gaveIdNumber = false)
        {
            if (string.IsNullOrWhiteSpace(maskedText)) return Stance.Hesitate;
            string text = maskedText.ToLowerInvariant();

            // A redacted number alone proves nothing ("ผมจะโทร [REDACTED] หาลูกก่อน"), so the words decide.
            // Only a reply that is nothing but the number is the player handing it over.
            string token = PIIMasker.Redacted.ToLowerInvariant();
            if (text.Contains(token))
            {
                text = text.Replace(token, " ");
                if (!HasLetterOrDigit(text)) return Stance.Comply;
            }

            int comply = 0;
            int hesitate = 0;
            int resist = 0;

            comply += Take(ref text, AgreePhrases);
            Take(ref text, NeutralPhrases);
            hesitate += 2 * Take(ref text, HesitatePhrases);
            resist += 2 * Take(ref text, ResistPhrases);

            // "ไม่" in front of a comply word turns it into a refusal: ไม่ได้, ไม่โอน, ไม่ยอม, ไม่ตกลง…
            foreach (string phrase in ComplyPhrases)
            {
                string negated = Not + phrase;
                if (!text.Contains(negated)) continue;
                resist += 2;
                text = text.Replace(negated, " ");
            }

            // A leftover "ไม่" leans towards refusal, but too weakly to decide on its own.
            if (text.Contains(Not)) resist += 1;

            foreach (string word in QuestionWords)
            {
                if (text.Contains(word)) resist += 1;
            }

            foreach (string phrase in ComplyPhrases)
            {
                if (text.Contains(phrase)) comply += 1;
            }

            // Handing over an ID number is going along, unless the words around it push back.
            // "ไม่โอน แต่เลขบัตรผมคือ …" must not become a comply: in the scripted scenario comply
            // means doing what the scammer just asked, which here would be the transfer.
            if (gaveIdNumber && resist == 0) comply += 2;

            if (resist > comply && resist >= hesitate) return Stance.Resist;
            if (comply > resist && comply > hesitate) return Stance.Comply;
            return Stance.Hesitate;
        }

        /// <summary>Counts the phrases found in the text and removes them from it.</summary>
        static int Take(ref string text, string[] phrases)
        {
            int found = 0;
            foreach (string phrase in phrases)
            {
                if (!text.Contains(phrase)) continue;
                found++;
                text = text.Replace(phrase, " ");
            }
            return found;
        }

        static bool HasLetterOrDigit(string text)
        {
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) return true;
            }
            return false;
        }
    }
}
