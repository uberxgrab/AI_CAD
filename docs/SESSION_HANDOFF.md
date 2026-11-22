# Bàn giao các session Claude Code: hvac_ai

Cập nhật lúc 20/11/2026, 17:40. File này tóm tắt các session Claude Code của dự án,
để có thể clone repo về máy khác và làm tiếp. Quy tắc và kiến trúc dự án nằm trong
`CLAUDE.md` và `AGENTS.md`; file này chỉ ghi phần không có ở đó: lịch sử, quyết định
của người dùng, việc đang dở, và những thứ nằm ngoài git.

## 1. Làm tiếp trên máy khác

### Trước khi rời máy cũ
- `main` đang đi trước `origin/main` 2 commit (`78b4c10`, `6441405`: HVACSTRETCH
  chọn đối tượng mới), chưa push. `docs/` và 2 dòng mới trong `CLAUDE.md` chưa commit.
  Commit hết rồi `git push`.
- Các branch tính năng đều đã gộp vào `main`. Các worktree trong
  `C:\Users\Admin\hvac_ai_worktrees\` (duct, pipe, drain, powerwire, ctrlwire) đã
  gộp hết, không cần mang theo.

### Trên máy mới
1. Clone vào **đúng** `C:\Users\Admin\hvac_ai`. `start.scr` và `.csproj` gắn cứng
   đường dẫn này và đường dẫn cài AutoCAD 2023.
   ```powershell
   git clone https://github.com/uberxgrab/AI_CAD.git C:\Users\Admin\hvac_ai
   ```
2. Cài AutoCAD 2023 (cần để build, vì add-in tham chiếu DLL của AutoCAD), .NET SDK
   hoặc Build Tools có targeting pack .NET Framework 4.8, và Python.
3. Tạo môi trường Python:
   ```powershell
   python -m venv venv
   venv\Scripts\pip install -r requirements.txt
   ```
4. Build và chạy test (lệnh đầy đủ ở mục Commands của `CLAUDE.md`):
   ```powershell
   dotnet build cad_addin\HvacAiCadAddin.csproj -p:Configuration=Debug -p:Platform=x64
   ```
5. Cho AutoCAD tự nạp DLL. Máy cũ dùng registry demand-load
   (`Applications\HvacAiCadAddin`, `LOADCTRLS 2`) trỏ vào `cad_addin\bin\Debug\net48`.
   Trên máy mới phải tạo lại, hoặc dùng `NETLOAD`. Khi AutoCAD đang mở, build không
   ghi đè được DLL.
6. Mở Claude Code trong thư mục repo và gửi câu mở đầu:
   > Đọc CLAUDE.md, AGENTS.md và docs/SESSION_HANDOFF.md. Tạo lại memory theo mục 6
   > của SESSION_HANDOFF.md, rồi báo cho tôi các việc đang dở ở mục 4.

### Những thứ không có trong git
| Thứ | Ở máy cũ | Trên máy mới |
| --- | --- | --- |
| Memory của Claude | `%USERPROFILE%\.claude\projects\c--Users-Admin-hvac-ai\memory\` | Tạo lại từ mục 6, hoặc chép nguyên thư mục |
| Lịch sử session (`*.jsonl`) | cùng thư mục cha ở trên | Không bắt buộc. Muốn `claude --resume` thì chép nguyên thư mục `c--Users-Admin-hvac-ai` sang đúng vị trí, với cùng đường dẫn dự án. File có toàn bộ hội thoại, **không đưa lên git**. |
| `venv/`, `cad_addin/bin`, `obj` | trong repo, bị `.gitignore` | Tạo lại ở bước 3–4 |
| Thiết lập HVACQTO lần trước | `%APPDATA%\HvacAiCadAddin\HVACQTO.settings` | Tự tạo khi chạy lệnh |
| Mẫu Excel tham khảo | `Desktop\vrv.xlsx`, `Desktop\chiller.xlsx` | Chỉ là tham khảo; lệnh không đọc khi chạy |
| Bản DXF của `MEP_DWT_3.dwg`, script của agent | thư mục scratchpad tạm | Chuyển lại bằng `accoreconsole` khi cần (xem `CLAUDE.md`) |

Agent con đang chạy dở **không** tiếp tục được trên máy khác; giao việc lại từ đầu.

## 2. Trạng thái hiện tại

- Branch `main`, HEAD `6441405`, chưa push 2 commit cuối. Commit `5213036`
  ("Initial commit") của người dùng xoá `cad_addin/src/Message.cs`, nên lệnh
  `FIRSTMESSAGE` không còn, dù `CLAUDE.md` vẫn liệt kê nó.
- Hai tag để khôi phục HVACSTRETCH khi có lỗi:
  - `hvacstretch-before-s1`: trước khi đổi cách tính khung bao (S1);
  - `hvacstretch-before-selection`: trước khi đổi cách chọn đối tượng (20/11).
- Các lệnh đã có trong add-in:

| Lệnh | Việc | Đã thử trong AutoCAD? |
| --- | --- | --- |
| `HVACSTRETCH` | Stretch hỗn hợp: STRETCH gốc cho đối tượng thường, `Distance3` cho block động. Từ 20/11 chọn đối tượng như STRETCH gốc (quét nhiều lần, Shift + click để bỏ) | Phần kéo đã được người dùng xác nhận chạy tốt sau bản sửa `9564ea0`. Phần chọn đối tượng mới chưa thử |
| `HVACQTO` | Bóc khối lượng ra `.xml` theo mẫu VRV, có công thức, hệ số dự phòng ở F6, ống trục | Bản có công thức chưa được ghi nhận đã thử |
| `HVACCOUNT` | Cộng chiều dài line/pline trên layer `!...x...` | Bản cũ |
| `HVACDUCT` | Vẽ tuyến ống gió bằng block `REC_WxH` / `Round D`, tự sinh phụ kiện polyline | Đã thử một phần |
| `HVACDUCTALIGN` | Dóng mép ống như Align của Revit; tự nối ống song song, tự thêm chân rẽ | Đã sửa theo phản hồi 19/11 |
| `HVACPIPESIZE` | Tách một đoạn ống nước và đổi sang layer cỡ ống | Chưa |
| `HVACREFRIG` | Nắn tuyến ga sơ bộ, chèn `Branch Connection (1)`, kiểm quy tắc, sizing theo Daikin VRV IV | Người dùng gặp lỗi không tạo được bộ chia ga (19/11); bản mới chưa thử |
| `HVACDRAIN` | Nắn và sizing ống nước ngưng, vát 45° × 300 mm | Chưa |
| `HVACPOWERWIRE` | Vẽ dây điện cấp nối tiếp cho dàn lạnh, có móc 45° | Chưa |
| `HVACCTRLWIRE` | Vẽ dây điều khiển song song từ thermostat tới từng dàn lạnh | Chưa |

- Mỗi lệnh có mục "Choices the code made" trong `AGENTS.md`: các lựa chọn agent tự
  quyết, cần người dùng xem lại.
- Test tự động: `tests/HvacQtoCoreTests`, `tests/PipeCoreTests`,
  `tests/DuctCoreTests`, `tests/DuctAcadTests` (chạy headless) và
  `tests/test_pipeline.py`.

## 3. Lịch sử các session

| Ngày | Session | Nội dung chính |
| --- | --- | --- |
| 05/11–14/11 | `3acba7c2` | Tạo `CLAUDE.md` từ `AGENTS.md`. Làm lại HVACQTO bằng một agent code và một agent test độc lập. Thêm ống đồng dạng `RP a-b`. Thêm công thức Excel, hệ số dự phòng, ống trục kỹ thuật. Debug C#: Antigravity không dùng được debugger C# của Microsoft, nên debug bằng VS Code chính hãng. |
| 06/11–12/11 | `648d40c6` | Khôi phục session cũ, chuyển session sang máy khác, Claude web. Tiếng Việt tốn token hơn: báo cáo cho người dùng bằng tiếng Việt, prompt cho agent bằng tiếng Anh. Cách để AutoCAD tự nạp DLL. |
| 06/11 | `737c13fd` | Cách chia task theo agent và session; chạy code-review bằng agent mới. |
| 10/11–20/11 | `025d64cd` | Tạo agent `research`. Nghiên cứu công cụ AI cho CAD và các dịch vụ model (MaaS). Ngày 20/11: hỏi về MCP, Gemini, Copilot giá rẻ; review lại toàn bộ code để xem nghiên cứu AI giúp được gì. Xem mục 7. |
| 14/11–20/11 | `f196b7b5` | **Session chính.** `/init`; được chỉ định làm điều phối. Nghiên cứu, lập kế hoạch rồi làm HVACDUCT, HVACDUCTALIGN, HVACPIPESIZE, HVACREFRIG; sửa S1–S3; thêm quy tắc và sizing cho HVACREFRIG; thêm HVACDRAIN, HVACPOWERWIRE, HVACCTRLWIRE. Cuối ngày 20/11: sửa cách chọn đối tượng của HVACSTRETCH. |
| 18/11 | `b10065ed` | Hỏi về usage: hạn mức tính theo tài khoản, không reset khi mở session mới. |
| 19/11 | `8883f6eb` | Tạo registry để AutoCAD tự nạp `bin\Debug\net48`. |
| 19/11–20/11 | `dcac3588` | Hỏi đáp về HVACREFRIG (không tạo được bộ chia ga) và đọc lại code. |
| 18/11–20/11 | `a46f08d0` | Session viết file này. |

## 4. Việc đang dở

1. **Thử tay phần chọn đối tượng mới của HVACSTRETCH** (đã làm xong ngày 20/11,
   commit `78b4c10` và `6441405`, build không lỗi, chưa thử trong AutoCAD).
   - Người dùng duyệt với điều kiện chỉ sửa phần chọn đối tượng (`GetSelection`).
     Phần phân loại đối tượng thường và block động, cách kéo và preview giữ nguyên.
   - Cách làm: quét nhiều lần, click thêm, Shift + click để bỏ, Enter/Space để chốt.
     Lệnh phát lại cho STRETCH gốc: `_C`/`_W` cho từng vùng, `_R (handent "...")` cho
     đối tượng đã bỏ, `(handent "...")` cho đối tượng click. Mỗi block động dùng đúng
     vùng đã chọn nó; block được click thì dời nguyên khối.
   - Quét một lần, không bỏ gì thì chuỗi gửi STRETCH giống hệt bản cũ.
   - Rủi ro chưa kiểm: `(handent "...")` có chạy qua `SendStringToExecute` không. Nếu
     không, Shift + click bỏ và click thêm sẽ không có tác dụng, và phải đổi cách làm.
   - Các bước thử, theo thứ tự: tắt AutoCAD, build, mở lại; quét crossing một lần;
     Shift + click bỏ một đường thẳng rồi một block động; quét hai vùng; click thêm
     một đối tượng sau khi quét; quét window trái sang phải; gõ `CP` vẽ đa giác; Esc ở
     từng bước; Enter khi chưa chọn gì (phải báo "No objects were selected.").
   - Lỗi thì khôi phục từ tag `hvacstretch-before-selection`.
2. **Thử tay trong AutoCAD** các lệnh còn "Chưa" ở bảng mục 2, nhất là HVACREFRIG
   bản có sizing.
3. **Sửa tài liệu:** bỏ `FIRSTMESSAGE` khỏi `CLAUDE.md`. Thêm HVACDUCT và
   HVACDUCTALIGN vào bảng lệnh của `CLAUDE.md` (hiện chỉ có trong `AGENTS.md`).
4. **Đề xuất giảm usage chưa áp dụng (18/11):** đặt `model: sonnet` cho
   `.claude/agents/research.md`; đặt biến `CLAUDE_CODE_SUBAGENT_MODEL=sonnet`. Người
   dùng đã dừng việc này, nhưng các lần giao việc sau đó vẫn dùng agent Sonnet.
5. **Tầng 0 và lệnh `HVACROOMS` (21/11, chưa commit, chờ thử tay):** người dùng chọn tầng 0
   trước MCP, rồi chọn hướng **bán tự động** (không dùng TraceBoundary). 5 bản vẽ trong
   `CAD/` chưa commit.
   - Python: `tcvn3.py`; `arch_extract.py` (prototype tự động); `arch_rooms.py` (phía sau
     của HVACROOMS, đọc `request.json`, ghi `rooms.json`, lõi polygonize: dây cung cửa,
     bịt ô mở giữa hai đầu tường ≤ 1800 mm, nối khe ≤ 50 mm). Hộp kỹ thuật nhận theo
     **chữ X** (hai nét chéo) hoặc chữ HKT/gen/shaft, theo yêu cầu người dùng; vùng quét
     cánh cửa không còn bị tính là trục kỹ thuật. Layer `HVAC-ROOM-SPLIT` luôn là tường.
   - C#: `HvacRooms.cs`, `RoomsCore.cs`, `RoomsExchange.cs`, `RoomsWriter.cs`,
     `RoomsSettingsForm.cs`; `.csproj` có `AcadDir` (mặc định 2023). Build được với
     AutoCAD 2024. Test: `tests/RoomsCoreTests` (285 kiểm tra), 20 test Python.
   - Đã chạy ngầm cả chuỗi (snapshot → Python → ghi → chạy lại thay thế) trong
     `accoreconsole` 2024 trên bản sao `P-KT` tầng 9: 19 phòng, 2 hộp kỹ thuật.
   - Việc tiếp: người dùng cài AutoCAD 2023 (nếu không được thì dùng 2021) và thử tay
     hộp thoại, preview, Yes/No, Undo. Giới hạn đã biết: không gian mở bị gộp (vẽ nét
     `HVAC-ROOM-SPLIT`), tên phòng là chữ dài nhất trong phòng, nhãn `DT.` của bản vẽ mẫu
     là diện tích cả căn.
   - Máy mới: venv ở `venv\` (Python 3.12 cài bằng winget); matplotlib chỉ cài trong venv
     để vẽ kiểm tra, không có trong `requirements.txt`. AutoCAD trên máy này là 2021/2024.
6. **Ba tầng sau tầng 0 (21/11, làm song song bằng 3 agent Sonnet theo yêu cầu người dùng,
   chưa commit, chưa thử tay):** người dùng chọn tính tải = diện tích × W/m² × hệ số dự
   phòng; **bảng W/m² phải do người dùng gửi** (hiện là giá trị giữ chỗ trong
   `heat_load_table.json`). Lệnh `HVACLOAD` (ghi `HVACAI_LOAD`, nhãn kW, CSV),
   `HVACPLACEUNITS` (chọn cassette `FCU-CAS-<kW> kW` theo index Daikin, đặt trong phòng,
   layer `EIA_1 MECHANICAL EQUIPMENT`), `HVACROUTEDRAFT` (cây nét vuông góc về trục kỹ
   thuật trên `HVAC-DRAFT-*` để chạy tiếp HVACREFRIG/HVACDRAIN/HVACPOWERWIRE). Build
   chung 0 cảnh báo; mọi bộ test Python và C# qua. Ngày 22/11 người dùng đã thử tay cả
   chuỗi trong AutoCAD: tương đối ít lỗi, các lỗi còn lại chưa được liệt kê, người dùng
   muốn giải quyết khi có AI hỗ trợ (xem memory ai-mcp-integration, các phase 0–4 MCP).
   Vẫn chờ bảng W/m² của người dùng.
6. **Kế hoạch cũ chưa làm:** lệnh bóc van và bóc thiết bị qua attribute (tư liệu ở
   `AGENTS.md`); schema MEP chung; tính tải lạnh; dữ liệu lỗi có gán nhãn cho AI.

## 5. Quyết định đã chốt với người dùng

**HVACQTO**
- Xuất `.xml` (SpreadsheetML), không phải `.xlsx`. Ống thép `SCH40`. Hệ số dự phòng
  mặc định 1.0, nằm ở ô F6 sheet `DIEU HOA THONG GIO`. Thiết lập nhớ giá trị lần trước.
- Ống đồng nhận `a/b` và `RP a-b`, bỏ phần trong ngoặc. Thành ống đồng D12.7–D41.3
  dày 1.0 mm; không đề xuất lại 1.1 mm.
- `CWS`/`CWR` là ống thép (S2). Phụ kiện ống gió có xdata `HVACAI_FITTING` không được
  bóc (S3).

**Ống gió**
- Phụ kiện là polyline, không phải block, và không bóc khối lượng.
- Co chữ nhật R tâm = 0.75W, theo thói quen của người dùng, không phải 1.5W của SMACNA.
- Côn: L = max(W_lớn/2, 1.207·ΔW, 1.207·ΔH); côn lệch tâm L = max(W_lớn/2, 1.732·ΔW).
- Chân rẽ: vát 45°, L = max(Wb/2, 102 mm), miệng Wb + L, không có cổ thẳng.
- HVACDUCT: chiều dài lấy bằng click điểm, hoặc Enter để dùng chiều dài trong hộp
  thoại.
- HVACDUCTALIGN: gặp hai ống song song thì nối luôn không hỏi, gộp thành một block
  từ đầu ống thứ nhất đến cuối ống thứ hai; nhánh vuông góc thì tự thêm chân rẽ.
- HVACSTRETCH: block nằm trọn trong vùng quét thì dời nguyên khối, giữ kích thước.

**Ống nước và ống ga**
- Bộ chia ga là `Branch Connection (1)`, tỉ lệ mặc định 1.24, cắt ống ở cả 3 cổng, đặt
  trên layer RP của phía vào.
- HVACPIPESIZE có thêm layer CWS/CWR/HWS/HWR DN15–DN600.
- Quy tắc ống ga: hai bộ chia cách nhau ≥ 1000 mm theo ống; từ đầu ra bộ chia tới chỗ
  gấp hoặc bộ chia kế tiếp ≥ 500 mm thẳng; từ bộ chia đầu tới dàn lạnh xa nhất ≤ 40 m,
  cảnh báo khi > 37 m (3 m dung sai thi công). Bộ chia lùi khỏi điểm T một đoạn
  D ≥ 500 mm, chỉnh tới lui để đạt quy tắc; không đạt thì khoanh tròn và cảnh báo.
- Sizing ống ga theo bảng Daikin VRV IV (RXYQ-T/U), công suất nhập theo index Daikin
  (kW = index × 0.112). Không ghi gì trước khi bấm Apply. Nhãn `%%C15.9/9.5`, cỡ chữ
  chọn được (mặc định 150).
- HVACDRAIN: cỡ nhỏ nhất D27; nhánh nối vào ống chính bằng đoạn 45° dài 300 mm; góc
  90° được vát.
- HVACPOWERWIRE: layer `EIA_5 SUPPLY WIRE`, móc 45° 300 × 300, dàn cuối chỉ có đoạn
  xuống.
- HVACCTRLWIRE: mỗi dàn một polyline trên `EIA_5 CONTROL WIRE`, cách nhau k × 150 mm,
  tách riêng ngay từ đầu thermostat, không cắt nhau.

**Cách làm việc**
- Session chính điều phối và được tự tạo agent con. Kế hoạch cho thay đổi lớn vẫn phải
  được duyệt trước.
- Dùng agent Sonnet để tiết kiệm usage, chạy tuần tự từng agent. Chỉ chạy song song khi
  người dùng yêu cầu (ví dụ trước giờ reset usage).
- Bỏ agent test độc lập. Không tự chạy test khi người dùng chưa yêu cầu.
- Sửa trực tiếp trong thư mục chính. Việc làm trong worktree phải gộp về thư mục chính,
  vì AutoCAD nạp DLL build từ đó.
- Không đụng vào AutoCAD hoặc Excel đang mở của người dùng. `MEP_DWT_3.dwg` và mẫu
  Excel chỉ đọc.
- Trả lời bằng tiếng Việt. Tên định danh, commit message và comment code bằng tiếng Anh.

## 6. Memory của Claude (để tạo lại trên máy mới)

Lưu mỗi ý dưới đây thành một memory:
1. **main-session-delegation** (feedback): session chính điều phối, được tự tạo agent
   con; kế hoạch thay đổi lớn vẫn cần duyệt; trả lời bằng tiếng Việt.
2. **duct-fittings-are-polylines** (project): phụ kiện ống gió là polyline, HVACQTO
   không bóc; không thiết kế block hay dòng BOQ cho phụ kiện trừ khi người dùng đổi ý.
3. **edit-location** (feedback): sửa trong thư mục chính; gộp việc ở worktree về thư mục
   chính mà không cần hỏi.
4. **hvac-draw-commands-decisions** (project): quyết định ngày 18–19/11 cho ống gió, ống
   ga, S1–S4 (mục 5). Nếu HVACSTRETCH lỗi sau S1 thì khôi phục từ tag
   `hvacstretch-before-s1`.
5. **sizing-and-wire-commands** (project): quyết định ngày 20/11 cho sizing HVACREFRIG,
   HVACDRAIN, HVACPOWERWIRE, HVACCTRLWIRE (mục 5).

## 7. Kết quả nghiên cứu AI (14/11, chưa áp dụng)

- AI của Autodesk (Assistant, MCP server) chỉ có từ AutoCAD 2027 và gần như chỉ đọc.
  Design Automation cho AutoCAD 2023 đã ngừng hỗ trợ. Chưa có sản phẩm nào tự đi ống
  2D trong AutoCAD đáng tin cậy.
- Kiến trúc đề xuất: Python chạy ngoài AutoCAD, trao đổi `snapshot.json` và
  `proposals.json` với add-in. AI chỉ đề xuất, người dùng duyệt từng mục, mỗi lần áp
  dụng là một nhóm Undo.
- Đọc bản vẽ DWG/DXF bằng code hình học trước (ezdxf, shapely). AI chỉ dùng cho phần
  ngữ nghĩa, hoặc khi đầu vào là PDF hay ảnh. Tuyến ống và vị trí máy do thuật toán
  sinh; LLM chỉ chọn và xếp hạng phương án.
- Giá tham khảo (USD mỗi triệu token vào/ra, theo agent, tháng 11/2026): Claude Haiku
  5.5 0.10/0.50, Sonnet 5.5 2/10, Opus 5.5 4/20; OpenAI và Gemini cùng mức. Chi phí
  token nhỏ so với công kỹ sư.
- Việc nên làm trước: chạy thử tách phòng, tường bao, trục kỹ thuật từ 3–5 bản vẽ
  kiến trúc thật, rồi mới chọn model.

**Bổ sung 20/11: MCP, Gemini, Copilot** (theo agent research, chưa tự kiểm lại)
- MCP không nhúng model vào CAD. Nó chỉ là giao thức để ứng dụng AI gọi tool mình
  viết; model vẫn chạy ở nhà cung cấp hoặc máy local. Cách hợp lý: một MCP server
  Python chạy ngoài AutoCAD, có tool chỉ đọc (`get_snapshot`, `classify_layers`) và
  tool `propose_*` ghi file JSON đề xuất. Việc ghi vào bản vẽ đi qua một lệnh add-in
  có preview, xác nhận, khớp theo handle và nằm trong một undo group.
- Client dùng được: Claude Code, GitHub Copilot trong VS Code (kể cả gói Free),
  Gemini CLI. MCP của AutoCAD 2027 chỉ cho Autodesk Assistant dùng và chỉ đọc.
- Gemini API: gói miễn phí có, nhưng dữ liệu bị dùng để huấn luyện, chỉ dùng cho bản
  vẽ mẫu. Gói trả phí prepay tối thiểu 5 USD; Flash-Lite khoảng 0,01–0,03 USD mỗi
  bản vẽ, không dùng dữ liệu để huấn luyện. Gemini CLI không còn miễn phí từ
  18/06/2026. Credit 300 USD của Google Cloud không dùng được cho Gemini API.
- Copilot: gói Free và Pro (10 USD/tháng) đều có MCP và agent mode trong VS Code; có
  thể dùng dữ liệu để huấn luyện, cá nhân opt-out được. GitHub Models đã đóng từ
  30/07/2026. Azure cho tài khoản mới 200 USD trong 30 ngày. Microsoft 365 Copilot
  không đọc DWG/DXF.
- Khuyến nghị: MCP server Python chỉ đọc cộng đề xuất, thử với Claude Code hoặc
  Copilot Free; khi cần LLM thì gọi Gemini API trả phí với Flash-Lite từ pipeline
  Python.

**Review code ngày 20/11: AI giúp được gì**
- Phần hạ nguồn đã có và có test: đi ống, sizing từ tuyến sơ bộ, công suất, bóc khối
  lượng. Phần thiếu là thượng nguồn: đọc kiến trúc, tìm trục và tường bao, đặt máy,
  vẽ tuyến sơ bộ. Chưa có tìm đường tự động, chưa có tính tải lạnh, chưa có hợp đồng
  JSON/HTTP giữa add-in và Python.
- Điểm nối gọn nhất (suy luận, chưa kiểm): AI hoặc thuật toán chỉ cần vẽ LINE/LWPOLYLINE
  sơ bộ trên một layer đề xuất, rồi người dùng chạy `HVACREFRIG`, `HVACDRAIN`,
  `HVACDUCT` có sẵn để nắn, sizing và ghi. Như vậy giữ được preview và undo của các
  lệnh hiện tại.
- Python đã có ezdxf, shapely, networkx cho tầng 0, nhưng `parser.py` cần đọc thêm
  TEXT/MTEXT và ARC.

## 8. Mẹo môi trường

- AutoCAD giữ DLL đã nạp cho tới khi thoát, nên sau khi build phải khởi động lại
  AutoCAD.
- Debug: dùng VS Code chính hãng, attach vào `acad.exe` kiểu `clr`. Antigravity không
  debug C# .NET Framework được.
- Startup Suite của APPLOAD không nạp `.dll` ổn định; dùng một file `.lsp` gọi
  `NETLOAD`, hoặc registry demand-load như ở bước 5 mục 1.
- Usage tính theo tài khoản (cửa sổ 5 giờ và theo tuần), không reset khi mở session
  mới. Session mới chỉ làm mới context.
