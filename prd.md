# PRD — ScamSim v0.1 (Prototype: ฉากแรก)

> สำหรับ Claude Code · Unity 6 (LTS) · C# · เป้าหมาย: เล่นได้จบ 1 ฉากภายใน 2 สัปดาห์
> Repo: `github.com/attachaibainana/scamsim` (public, MIT)

---

## 0. บริบท (อ่านก่อนเริ่ม)

ScamSim คือ **research testbed** ไม่ใช่เกมเพื่อความบันเทิง เป้าหมายปลายทางคือใช้วัดว่า "คนทั่วไปทนต่อ scam ที่ขับเคลื่อนด้วย AI ได้แค่ไหน และฝึกได้ไหม" — ทุก design decision ให้ยึดหลัก: **เก็บข้อมูลได้ / ทำซ้ำได้ / วิเคราะห์ได้** ก่อนความสวยงาม

v0.1 นี้พิสูจน์แค่ 3 อย่าง:
1. LLM เล่นบท scammer แล้ว**ปรับบทตามคำตอบผู้เล่น**ได้จริง
2. ระบบ log ทุก decision ลงไฟล์ได้
3. Feedback ตอนจบบอกได้ว่าผู้เล่นพลาด red flag ไหน

ไม่ทำใน v0.1: voice, deepfake, หลาย scenario, UI สวย, SRS, multiplayer, backend

---

## 1. Core loop (1 ฉาก)

```
[Intro] → [สายเข้า] → [บทสนทนา 6-12 turns] → [จบสาย: โดนหลอก / รอด / วางสาย] → [Debrief]
```

**Scenario v0.1: "ตำรวจไซเบอร์ปลอม"** (call center scam ที่พบบ่อยสุดในไทย)
- Scammer อ้างเป็นตำรวจ บอกว่าชื่อผู้เล่นพัวพันคดีฟอกเงิน
- เป้าหมาย scammer: ให้ผู้เล่น (a) บอกเลขบัตร ปชช. (b) โอนเงิน "เพื่อตรวจสอบ" (c) โหลดแอป/กดลิงก์
- เป้าหมายผู้เล่น: วางสายโดยไม่เสียอะไร หรือจับได้ว่าเป็น scam

---

## 2. Functional requirements

### 2.1 Conversation engine
- ผู้เล่นตอบได้ 2 แบบในทุก turn: **เลือก 3 ตัวเลือก** (LLM generate ให้: 1 ตอบตาม / 1 ลังเล / 1 ปฏิเสธ-ตั้งคำถาม) หรือ **พิมพ์เอง** (free text)
- Scammer (LLM) ตอบกลับโดยอ้างอิง conversation history ทั้งหมด
- Scammer มี **pressure level 1-5** ที่เพิ่มเมื่อผู้เล่นขัดขืน (เสียงดุขึ้น, ขู่, เร่ง) และลดเมื่อผู้เล่นคล้อยตาม (ปลอบ, ให้ความหวัง) — ส่ง level นี้เข้า prompt ทุก turn
- จบสายเมื่อ: ผู้เล่นกด "วางสาย" / ผู้เล่นให้ข้อมูลครบตาม goal ของ scammer / ครบ 12 turns

### 2.2 LLM integration
- Provider: Anthropic API (Claude Sonnet) ผ่าน `UnityWebRequest` — ห้ามใช้ SDK ที่ไม่รองรับ WebGL
- API key อ่านจาก `StreamingAssets/config.json` (gitignored) — มี `config.example.json` ให้
- ทุก response ต้องเป็น **JSON strict** ตาม schema:
  ```json
  {
    "scammer_line": "string",
    "pressure_level": 1-5,
    "red_flags_in_this_line": ["string"],
    "player_options": ["string", "string", "string"],
    "goal_progress": { "id_number": bool, "transfer": bool, "link_click": bool },
    "call_should_end": bool,
    "end_reason": "scammed" | "escaped" | "hung_up" | null
  }
  ```
- Retry 2 ครั้งถ้า parse JSON ไม่ผ่าน แล้ว fallback เป็น scripted line
- Timeout 20 วิ → แสดง "สายหลุด" แล้วให้ลองใหม่ (ไม่ crash)

### 2.3 System prompt design (ไฟล์แยก `Prompts/scammer_police.txt`)
ต้องมี 5 ส่วน:
1. **Framing**: นี่คือ training simulator เพื่อสอนคนป้องกันตัว ผู้เล่นรู้ว่ากำลังฝึก
2. **Persona**: ชื่อ ยศ หน่วยงานปลอม สคริปต์เปิด เทคนิคกดดันที่ใช้ได้ (authority, urgency, fear, isolation)
3. **Adaptation rules**: ถ้าผู้เล่นถามเลขบัตร/ขอโทรกลับ → เบี่ยง ไม่ยอมแพ้ทันที / ถ้าผู้เล่นคล้อยตาม → เร่งไป goal ถัดไป
4. **Hard limits**: ห้ามให้เลขบัญชีจริง ห้ามพูดถึงบุคคลจริง ห้ามใช้ชื่อหน่วยงานจริงแบบเป๊ะ (ใช้ "กองปราบไซเบอร์กลาง" แทนชื่อจริง) ห้ามด่าหยาบ
5. **Output format**: JSON schema ข้างบน + ตัวอย่าง 1 turn

### 2.4 Logging (สำคัญที่สุดสำหรับงานวิจัย)
- ทุก session → 1 ไฟล์ `Logs/session_{timestamp}.json`
- บันทึกต่อ turn: `turn_index, timestamp, scammer_line, pressure_level, red_flags, player_options, player_choice (index หรือ "free"), player_text, response_time_ms, goal_progress`
- บันทึกต่อ session: `session_id, scenario_id, start/end time, end_reason, total_turns, red_flags_total, red_flags_player_reacted_to`
- มีปุ่ม "Export CSV" ใน debrief → flatten ทุก turn เป็นแถว
- **ห้ามเก็บข้อมูลส่วนตัวจริงของผู้เล่น** — ถ้าผู้เล่นพิมพ์เลข 13 หลักหรือเบอร์โทรจริง ให้ mask เป็น `[REDACTED]` ก่อน log และก่อนส่ง LLM

### 2.5 Debrief screen
- ผลลัพธ์: โดนหลอก / รอด / วางสายทัน
- Timeline บทสนทนาแบบย่อ highlight red flags ที่ scammer ปล่อยออกมา และ mark ว่าผู้เล่น**เห็น/ไม่เห็น** (วัดจากคำตอบ turn ถัดไป — ถ้าตอบแบบปฏิเสธ/ตั้งคำถาม = เห็น)
- สรุป 3 บทเรียนสั้นๆ (LLM generate จาก log)
- ปุ่ม: เล่นอีกครั้ง / Export CSV

### 2.6 News screen ("สถานการณ์ล่าสุด")
หน้ารวมข่าวคนถูกหลอกจริง เพื่อให้ผู้เล่นตามทันกลโกงปัจจุบัน และเป็นแหล่ง scenario ในอนาคต

**v0.1 (ทำตอนนี้):**
- อ่านจาก `StreamingAssets/news.json` ที่ bundle มากับเกม (ทำมือ 10 ข่าว)
- แสดงเป็น list: วันที่ / headline / สรุป 2 บรรทัด / tag ประเภท scam / ความเสียหาย / ปุ่ม "อ่านต้นฉบับ" (เปิด browser)
- กรองตาม tag ได้
- ปุ่ม "ฝึกรับมือเหตุการณ์นี้" **แสดงแต่ disabled** พร้อม label "เร็วๆ นี้"

**Schema `news.json`:**
```json
{
  "updated_at": "ISO-8601",
  "items": [{
    "id": "string",
    "date": "YYYY-MM-DD",
    "headline": "string",
    "summary": "string (LLM เขียนเอง ไม่ copy เนื้อข่าว)",
    "scam_type": "call_center | romance | investment | parcel | job | loan | other",
    "tactics": ["authority", "urgency", "fear", ...],
    "loss_thb": number | null,
    "victim_profile": "string | null",
    "source_name": "string",
    "source_url": "string",
    "playable": false
  }]
}
```

**ลิขสิทธิ์:** เก็บเฉพาะ headline + สรุปที่เขียนเอง + ลิงก์ + ชื่อแหล่ง — **ห้ามเก็บเนื้อข่าวเต็ม**

**v0.2 (ไม่ทำตอนนี้ แต่ออกแบบ schema เผื่อไว้แล้ว):**
- GitHub Actions cron ทุก 6 ชม. → ดึง RSS แหล่งข่าวไทย → กรอง keyword → LLM สรุป + tag → commit `news.json` → เกม fetch จาก raw GitHub URL (ไม่ต้องมี server)
- เปิดปุ่ม "ฝึกรับมือเหตุการณ์นี้" → LLM สร้าง scenario จาก summary + tactics ของข่าวนั้น → เล่นได้ทันที
- `news.json` ที่สะสมไปเรื่อยๆ = dataset สำหรับงานวิจัยโดยอัตโนมัติ

---

## 2.7 Art direction

**โทน:** flat 2D vector, น่ารัก-อ่านง่าย, ดูเป็น "เกม" ไม่ใช่แอปราชการ — คนอายุ 60 เปิดแล้วไม่กลัว

**กฎลายเส้น:**
- เส้นขอบหนา สีเข้มกว่า fill 2 ระดับ (ไม่ใช่ดำล้วน) ความหนาเท่ากันทั้งเกม
- รูปทรงกลมมน สัดส่วนป้อม ไม่มีมุมแหลม ไม่มี gradient/texture
- ตัวละคร: silhouette เดียวจำง่าย, ไม่มีหน้าตาละเอียด (ตาเป็นจุดหรือแถบเดียว), อารมณ์สื่อผ่านท่าทาง + สีพื้นหลัง
- **ต้องเป็นดีไซน์ต้นฉบับ** — ห้ามใช้ silhouette/ชุด/visor แบบตัวละครจากเกมที่มีอยู่ ให้ออกแบบตัวละครใหม่ (เช่น ตัวการ์ตูนทรงหยดน้ำ, ทรงกระบอก, สัตว์ประจำถิ่น) แล้วใช้ตัวเดียวกันทั้งเกม
- Palette: พื้นหลัง 2-3 สีเข้ม, ตัวละคร 6-8 สีสด, UI accent 1 สี — scammer ใช้สีแดง-ส้มเสมอ, ผู้เล่นใช้สีเขียว-ฟ้าเสมอ (สีเล่าเรื่องแทนหน้าตา)
- ฟอนต์: ไทย sans-serif หัวกลม (เช่น Noto Sans Thai / IBM Plex Sans Thai) ขนาดขั้นต่ำ 18px สำหรับผู้สูงอายุ

**ฉากโทรศัพท์:** มุมมองเป็นหน้าจอมือถือเต็มจอ, avatar scammer เป็นเงาดำมีแค่ขอบเรืองสีแดง (ไม่รู้หน้า = ตรงกับความจริง), bubble ข้อความทรงกลมมนขอบหนา, pressure level แสดงเป็นสีพื้นหลังที่ค่อยๆ แดงขึ้น

**Asset pipeline:** SVG → import เป็น Sprite (Unity Vector Graphics package หรือ export PNG @2x) — ไม่ใช้ asset store character pack

---

## 2.8 Tooling: Unity × Claude Code

ใช้ **Official Unity Plugin for Claude Code** (first-party จาก Unity) — ติดตั้งจาก Claude partner plugins directory (`claude.com/plugins/unity`) ตาม docs ที่ `docs.unity.com/en-us/ai/unity-plugin/claude-code`

สิ่งที่ได้:
- **29 skills** สำหรับ Unity โดยเฉพาะ — ที่ใช้ในโปรเจกต์นี้: `/new-unity-project`, UI Toolkit, 2D/sprites, WebGL optimization, localization
- **`/unity-cli`** — สั่ง Editor จาก terminal: สร้าง/แก้ GameObject, แก้ scene, รัน C#, build, อ่าน log
- **MCP server** ควบคุม Editor ที่เปิดอยู่แบบ live

กฎการใช้:
- UI ใช้ **UI Toolkit** (Unity 6+) ตามที่ skill แนะนำ ไม่ใช้ uGUI
- ให้ Claude Code **build + อ่าน console log ผ่าน CLI ทุกครั้ง**ก่อน commit ไม่ใช่ให้คนเปิด Editor เช็คเอง
- ถ้า plugin ทำอะไรไม่ได้ (เช่น Unity version เก่ากว่า 6) → fallback เป็น `CoderGamester/mcp-unity` (open source) แต่ไม่ใช้พร้อมกัน

---

## 3. Non-functional

- **Platform**: Windows standalone ก่อน → WebGL build ต้องผ่านภายใน v0.1 (ใช้สำหรับ demo link)
- **ภาษา UI**: ไทยทั้งหมด, string อยู่ใน `Localization/th.json` เพื่อแปล EN ทีหลัง
- **ไม่มี backend** — ทุกอย่าง local
- **Offline fallback**: ถ้าไม่มี API key ให้รัน scripted scenario (branching tree 3 ระดับ) เพื่อให้ demo ได้เสมอ
- **Reproducibility**: ตั้ง `temperature` ได้จาก config, บันทึก model id + prompt version ลง log ทุก session

---

## 4. Architecture (แนะนำ)

```
Assets/
  Scripts/
    Core/
      GameSession.cs        — state machine: Intro → Call → Debrief
      ConversationManager.cs — จัดการ turns, history, pressure
    LLM/
      ILLMProvider.cs       — interface
      AnthropicProvider.cs  — UnityWebRequest implementation
      ScriptedProvider.cs   — offline fallback
      ResponseParser.cs     — JSON validate + retry
    Logging/
      SessionLogger.cs
      CsvExporter.cs
      PIIMasker.cs
    UI/
      CallScreen.cs, DebriefScreen.cs, IntroScreen.cs, NewsScreen.cs
    News/
      NewsLoader.cs         — อ่าน local json (v0.1) → remote fetch (v0.2)
  Prompts/
    scammer_police.txt
  Scenarios/
    police_scripted.json    — offline tree
  Localization/th.json
  StreamingAssets/
    config.example.json
    news.json             — 10 ข่าวทำมือ (v0.1)
Logs/                        — gitignored
```

- ใช้ `ILLMProvider` interface เพื่อสลับ provider ได้ (จะเพิ่ม OpenAI/local model ทีหลัง)
- ไม่ใช้ asset store package ที่เป็น paid/closed source — repo ต้อง clone แล้ว build ได้

---

## 5. Acceptance criteria (v0.1 เสร็จเมื่อ)

- [ ] เล่นจบ 1 ฉากได้ทั้ง 3 แบบ (โดนหลอก / รอด / วางสาย)
- [ ] Scammer เปลี่ยนท่าทีชัดเจนเมื่อผู้เล่นขัดขืน vs คล้อยตาม (ทดสอบ 5 session เทียบกัน)
- [ ] ไฟล์ log ครบทุก field, export CSV เปิดใน Excel ได้
- [ ] PII ถูก mask จริง (ทดสอบพิมพ์เลข 13 หลัก)
- [ ] Offline mode เล่นได้ไม่ต้องมี key
- [ ] WebGL build รันในเบราว์เซอร์ได้
- [ ] หน้าข่าวแสดง 10 ข่าวจาก news.json + กรอง tag ได้ + เปิดลิงก์ต้นฉบับได้
- [ ] README (EN) มี: ปัญหา / วิธีทำงาน / วิธี setup / screenshot / roadmap

---

## 6. Roadmap หลัง v0.1 (ไม่ทำตอนนี้)

- v0.2: GitHub Actions news pipeline + ปุ่ม "ฝึกรับมือเหตุการณ์นี้" (LLM สร้าง scenario จากข่าว) + scenario เพิ่ม (romance, investment, parcel)
- v0.3: voice channel (TTS scammer + STT ผู้เล่น)
- v0.4: pre/post knowledge quiz + difficulty adaptation ข้าม session
- v1.0: แยก conversation engine เป็น Unity package

---

## 7. คำสั่งเริ่มต้นสำหรับ Claude Code

```
อ่าน PRD นี้ทั้งหมด แล้ว:
0. ติดตั้ง Official Unity Plugin for Claude Code (ข้อ 2.8) แล้วใช้ /new-unity-project สร้างโปรเจกต์ Unity 6 2D
1. สร้างโครง project ตาม Architecture ข้อ 4
2. เริ่มจาก ScriptedProvider + CallScreen ให้เล่น offline ได้ก่อน
3. แล้วค่อยทำ AnthropicProvider + ResponseParser
4. SessionLogger ทำพร้อมข้อ 2 ไม่ใช่ทีหลัง
5. ทุก commit ต้อง build ผ่าน
ถามก่อนตัดสินใจเรื่อง: JSON library, โครง scripted tree, ดีไซน์ตัวละครหลัก (เสนอ 3 แบบเป็น SVG ให้เลือกก่อนทำ sprite)
```