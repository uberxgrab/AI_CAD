# Bàn giao các session Claude Code: hvac_ai (AI_CAD)

Cập nhật lúc 23/11/2026. File này tóm tắt các session Claude Code của dự án, để có thể
clone repo về máy khác và làm tiếp. Quy tắc và kiến trúc dự án nằm trong `CLAUDE.md`
và `AGENTS.md`; kế hoạch phần AI nằm trong `docs/AI_MCP_PLAN.md`. File này chỉ ghi phần
không có ở đó: lịch sử, quyết định của người dùng, việc đang dở, và những thứ nằm ngoài
git.

## 1. Làm tiếp trên máy khác

### Trước khi rời máy hiện tại
- Toàn bộ việc từ 21/11 nằm trên nhánh **`hvac-room-chain`** (11 commit, từ `ec65a00`
  tới `358dd0d`), **chưa push, chưa gộp vào `main`**. `main` cũng có 2 commit HVACSTRETCH
  cũ (`78b4c10`, `6441405`) chưa push. Push cả hai nhánh trước khi đổi máy:
  ```powershell
  git push origin main
  git push -u origin hvac-room-chain
  ```
- 5 bản vẽ đánh giá trong `CAD/` **không có trong git** (chưa commit theo ý người dùng).
  Phải chép tay sang máy mới (xem bảng "Những thứ không có trong git").
- Hai tag khôi phục HVACSTRETCH (`hvacstretch-before-s1`, `hvacstretch-before-selection`)
  **không có trên máy hiện tại**; có lẽ chỉ còn ở máy cũ. Nếu còn máy cũ, push tag lên:
  `git push origin --tags`.

### Trên máy mới
1. Clone repo. Máy hiện tại dùng `C:\Users\Admin\AI_CAD`. `start.scr` và
   `StartArguments` trong `.csproj` vẫn gắn cứng đường dẫn cũ `C:\Users\Admin\hvac_ai`.
   ```powershell
   git clone https://github.com/uberxgrab/AI_CAD.git C:\Users\Admin\AI_CAD
   git -C C:\Users\Admin\AI_CAD switch hvac-room-chain
   ```
2. Cài AutoCAD 2021–2024 (add-in build cho .NET Framework 4.8; **AutoCAD 2025 trở lên
   dùng .NET 8 nên không nạp được add-in này**), .NET SDK có targeting pack .NET
   Framework 4.8, và Python 3.12. `cad_addin\AcadDir.props` tự chọn bản đã cài theo thứ
   tự 2023 → 2021 → 2024; muốn chọn bản khác thì thêm `-p:AcadDir="..."`.
3. Nếu repo nằm trong thư mục người dùng khác (máy hiện tại: repo ở `C:\Users\Admin`,
   tài khoản là `pc`) thì phải cấp quyền ghi, chạy PowerShell **Run as Administrator**:
   `icacls "C:\Users\Admin\AI_CAD" /grant "pc:(OI)(CI)M" /T`.
4. Tạo môi trường Python (`requirements.txt` là UTF-16 LE có BOM; đã có `mcp==2.3.0`
   và `matplotlib`):
   ```powershell
   py -3.12 -m venv venv
   venv\Scripts\pip install -r requirements.txt
   ```
5. Đặt danh tính git cho repo nếu máy chưa có (máy hiện tại đặt riêng cho repo:
   `git config user.name uberxgrab`, `git config user.email <email của người dùng>`).
6. Build, chạy test (lệnh đầy đủ ở mục Commands của `CLAUDE.md`):
   ```powershell
   dotnet build cad_addin\HvacAiCadAddin.csproj -p:Configuration=Debug -p:Platform=x64
   venv\Scripts\python.exe -m unittest discover -s tests
   ```
   Build có `-p:Platform=x64` ghi DLL vào `cad_addin\bin\x64\Debug\net48\`; `NETLOAD`
   đúng file đó (không phải `bin\Debug\net48` mà `start.scr` dùng).
7. Phần AI: mở thư mục repo trong VS Code, mở `.vscode/mcp.json`, bấm **Start** trên
   `hvac-cad`, rồi dùng Copilot Chat ở chế độ **Agent**. Cấu hình riêng máy: chép
   `aitools_config.example.json` thành `aitools_config.json` (bị `.gitignore`).
8. Mở Claude Code trong thư mục repo và gửi câu mở đầu:
   > Đọc CLAUDE.md, AGENTS.md, docs/SESSION_HANDOFF.md và docs/AI_MCP_PLAN.md. Tạo lại
   > memory theo mục 6 của SESSION_HANDOFF.md, rồi báo cho tôi các việc đang dở ở mục 4.

### Những thứ không có trong git
| Thứ | Ở máy hiện tại | Trên máy mới |
| --- | --- | --- |
| Memory của Claude | `C:\Users\PC\.claude\projects\c--Users-Admin-AI-CAD\memory\` (máy cũ: `...\c--Users-Admin-hvac-ai\memory\`) | Tạo lại từ mục 6, hoặc chép nguyên thư mục |
| Lịch sử session (`*.jsonl`) | thư mục cha của memory ở trên | Không bắt buộc; có toàn bộ hội thoại, **không đưa lên git** |
| 5 bản vẽ đánh giá | `CAD\` (untracked): `P-KT_dieu chinh_2.11.dwg`, `03.Nhà chống rung2-...dwg`, `17.1 LK-E9 BV KHI THAI.dwg`, `C1-DHTG-...dwg`, `D1-DHTG-...dwg` | Chép tay. Không chép các file khoá `.dwl`/`.dwl2` và `*_HVACLOAD.csv` |
| `venv/`, `cad_addin/bin`, `obj` | trong repo, bị `.gitignore` | Tạo lại ở bước 4 và 6 |
| Thiết lập các lệnh | `%APPDATA%\HvacAiCadAddin\*.settings` (HVACQTO, HVACROOMS, HVACLOAD, HVACPLACEUNITS, HVACROUTEDRAFT, HVACPLACEODU, HVACRISER…) | Tự tạo khi chạy lệnh |
| Cấu hình AI riêng máy | `aitools_config.json` (nếu có) | Chép từ `aitools_config.example.json` |
| Cache của MCP server | `%LOCALAPPDATA%\HvacAiCad\work\` (DXF đã chuyển, tổng quan, ảnh, log `mcp_server.log`) | Tự tạo lại |
| File tạm của HVACROOMS / HVACLOAD | `%TEMP%\HvacAiCadAddin\rooms\`, `...\load\` | Tự tạo |
| Mẫu Excel tham khảo | `Desktop\vrv.xlsx`, `Desktop\chiller.xlsx` (máy cũ) | Chỉ là tham khảo |

Agent con đang chạy dở **không** tiếp tục được trên máy khác; giao việc lại từ đầu.

## 2. Trạng thái hiện tại

- Nhánh `hvac-room-chain`, HEAD `358dd0d`. Thư mục làm việc sạch, trừ 8 file untracked
  trong `CAD/` (5 bản vẽ, 2 file khoá `.dwl`, 1 CSV do người dùng tạo khi thử HVACLOAD).
- Các lệnh trong add-in:

| Lệnh | Việc | Đã thử trong AutoCAD? |
| --- | --- | --- |
| `HVACSTRETCH` | Stretch hỗn hợp; từ 20/11 chọn đối tượng như STRETCH gốc | Phần kéo đã ổn; phần chọn đối tượng mới chưa thử |
| `HVACQTO` | Bóc khối lượng ra `.xml` theo mẫu VRV | Bản có công thức chưa ghi nhận đã thử |
| `HVACCOUNT` | Cộng chiều dài line/pline trên layer `!...x...` | Bản cũ |
| `HVACDUCT`, `HVACDUCTALIGN` | Vẽ và dóng ống gió | Đã thử một phần |
| `HVACPIPESIZE` | Tách một đoạn ống nước, đổi layer cỡ ống | Chưa |
| `HVACREFRIG` | Nắn tuyến ga, chèn bộ chia, sizing Daikin VRV IV | Bản có sizing chưa thử |
| `HVACDRAIN`, `HVACPOWERWIRE`, `HVACCTRLWIRE` | Ống nước ngưng, dây điện, dây điều khiển | Chưa |
| **`HVACROOMS`** (21/11) | Tìm phòng bán tự động: chọn mặt bằng, tick layer tường, Python `arch_rooms.py` chạy ngoài AutoCAD, preview, ghi `HVAC-ROOM` | **Người dùng đã thử 22/11**, tương đối ít lỗi |
| **`HVACLOAD`** (21/11) | Tải lạnh = diện tích × W/m² × hệ số; ghi `HVACAI_LOAD`, nhãn kW, CSV | **Đã thử 22/11** |
| **`HVACPLACEUNITS`** (21/11) | Chọn và đặt cassette `FCU-CAS-<kW> kW` theo index Daikin | **Đã thử 22/11** |
| **`HVACROUTEDRAFT`** (21–22/11) | Vẽ nét thô ga, nước ngưng, điện từ dàn lạnh về trục kỹ thuật; từ 22/11 nối vào cổng dàn lạnh và đi theo hành lang | Bản 21/11 đã thử; bản cổng + hành lang chưa |
| **`HVACPLACEODU`** (22/11) | Chọn HP dàn nóng theo tổng index, chèn `ODU-n HP` | Chưa |
| **`HVACRISER`** (22/11) | Ống đứng ga nhiều tầng: index cộng dồn, cỡ ống, bộ chia, bảng tổng hợp | Chưa |

- Chuỗi lệnh đầy đủ: `HVACROOMS → HVACLOAD → HVACPLACEUNITS → HVACPLACEODU →
  HVACROUTEDRAFT → HVACRISER → HVACREFRIG / HVACDRAIN / HVACPOWERWIRE → HVACQTO`.
- **Phần AI, Phase 1 đã xong (23/11):** `mcp_server.py` + gói `aitools/`. Chưa chạy thử
  trong Copilot.
- Test tự động (23/11, tất cả qua): 46 test Python (`discover -s tests`); C#:
  `RoomsCoreTests` 285, `LoadCoreTests` 168, `UnitCoreTests` 492, `RouteCoreTests` 45 978,
  `OduRiserCoreTests` 537, `PipeCoreTests` 20 982, `DuctCoreTests` 2 355,
  `HvacQtoCoreTests` 19 188. Test chạy ngầm trong `accoreconsole`: `tests/DuctAcadTests`,
  `tests/UnitCoreTests/Acad`, `tests/OduRiserCoreTests/Acad/run-tests.ps1`.

## 3. Lịch sử các session

| Ngày | Session | Nội dung chính |
| --- | --- | --- |
| 05/11–14/11 | `3acba7c2` | Tạo `CLAUDE.md` từ `AGENTS.md`. Làm lại HVACQTO. Thêm ống đồng `RP a-b`, công thức Excel, hệ số dự phòng, ống trục. |
| 06/11–12/11 | `648d40c6` | Khôi phục và chuyển session sang máy khác. Báo cáo bằng tiếng Việt, prompt cho agent bằng tiếng Anh. Cách để AutoCAD tự nạp DLL. |
| 06/11 | `737c13fd` | Cách chia task theo agent và session; code-review bằng agent mới. |
| 10/11–20/11 | `025d64cd` | Tạo agent `research`. Nghiên cứu công cụ AI cho CAD, MCP, Gemini, Copilot. Xem mục 7. |
| 14/11–20/11 | `f196b7b5` | **Session chính cũ.** HVACDUCT, HVACDUCTALIGN, HVACPIPESIZE, HVACREFRIG, HVACDRAIN, HVACPOWERWIRE, HVACCTRLWIRE; sửa chọn đối tượng HVACSTRETCH. |
| 18/11–20/11 | `b10065ed`, `8883f6eb`, `dcac3588`, `a46f08d0` | Usage; registry tự nạp DLL; hỏi đáp HVACREFRIG; viết bản bàn giao đầu tiên. |
| 21/11 | `be07b05a` | Đề xuất tích hợp AI qua MCP (FastMCP server, cầu nối named pipe, Claude Desktop, các phase 0–4) và nghiên cứu AI nhận dạng tường/ô mở. Xem memory `ai-mcp-integration`. |
| 21/11–23/11 | `54890397` | **Session chính hiện tại** (máy mới `PC`). `/init` sửa `CLAUDE.md`; cài Python; tầng 0 (đọc kiến trúc); HVACROOMS; HVACLOAD, HVACPLACEUNITS, HVACROUTEDRAFT (3 agent song song); HVACPLACEODU, HVACRISER, cổng dàn lạnh và đi theo hành lang (agent tuần tự); commit lên `hvac-room-chain`; kế hoạch AI 4 phase; so sánh host và giá; bảo mật; Phase 1 MCP server; review repo CAD-MCP. |

## 4. Việc đang dở

### 4.1 Chờ người dùng
1. **Bảng W/m² của người dùng.** `heat_load_table.json` đang là giá trị giữ chỗ (phòng ngủ
   150, khách 180, văn phòng 180, họp 250, sảnh 150, nhà hàng 250, bếp 250, khách sạn
   170, cửa hàng 200, gym 220, khác 180; hệ số 1,1). Có bảng thật thì chỉ thay file JSON.
2. **Vị trí cổng nối thật của cassette.** Template không có dấu cổng; `HVACROUTEDRAFT`
   đang giả định cổng ga (475, −300), cổng nước ngưng (475, −650) theo toạ độ block, ống
   ra theo +X. Sửa được trong hộp thoại của lệnh.
3. **Danh sách lỗi người dùng gặp khi thử 22/11** (người dùng nói "tương đối ít lỗi",
   muốn giải quyết khi có AI hỗ trợ, chưa liệt kê).
4. **Quyết định về khung chat trong AutoCAD** (lệnh `HVACAI`, khung dock, xem mục 7.4):
   có làm trong Phase 2 không, và có tạo tài khoản **Claude API** (nạp trước, đặt giới
   hạn chi phí, ví dụ $30/tháng) không. Copilot Free không có API cho ứng dụng tự viết.
5. **Duyệt đợt chỉnh nhỏ cho `mcp_server.py`** (học từ CAD-MCP, xem mục 7.5): đánh dấu tool
   chỉ đọc/phá huỷ, báo tiến độ cho `convert_dwg`/`drawing_overview`, đầu ra có cấu
   trúc, prompt mẫu "HVAC assistant", và dò thư mục cài AutoCAD qua registry
   (`AcadLocation`) cho `accoreconsole`.
6. **Duyệt kế hoạch Phase 2** (chưa trình): cầu nối add-in (named pipe) với các tool đề
   xuất có preview/Yes/No trong AutoCAD, cộng thêm đường **đọc bản vẽ đang mở qua COM**
   (`pywin32`; `live_layers`, `live_selection`, `find_rooms_in_selection`), kết nối
   đúng phiên AutoCAD đang chạy, bỏ qua AutoCAD 2025+.

### 4.2 Việc kỹ thuật
1. **Thử tay** `HVACPLACEODU`, `HVACRISER`, `HVACROUTEDRAFT` bản cổng + hành lang (các
   bước đã gửi người dùng 22/11), và thử Phase 1 trong Copilot (câu hỏi mẫu: "liệt kê bản
   vẽ, cho tôi các mặt bằng của P-KT, thống kê layer tầng 9 và gợi ý layer tường, rồi tìm
   phòng và tính tải lạnh").
2. **Push** `main` và `hvac-room-chain`, và sau khi thử ổn thì gộp `hvac-room-chain` vào
   `main`: **người dùng tự làm**. Thay đổi chưa commit hiện có: `docs/SESSION_HANDOFF.md`.
3. **Giới hạn đã biết, chưa sửa:**
   - Không gian mở bị gộp thành một phòng; người dùng tách bằng nét `HVAC-ROOM-SPLIT`.
   - Tên phòng là chữ dài nhất trong phòng, có khi nhặt nhầm ghi chú cửa.
   - Ô mở > 1800 mm và tường nét đơn có thể làm phòng rò.
   - Tuyến hành lang: các dàn lạnh trong cùng phòng thường nối tiếp nhau trong phòng;
     tăng chi phí trong phòng gần như không đổi, cần quy tắc mới.
   - `HVACRISER` ước tính chiều dài tương đương bằng chiều cao tầng 3600 mm.
   - Template chỉ có `ODU-6 HP`…`ODU-24 HP` (HP chẵn); không có cassette 2,2 kW và 8,0 kW
     (người dùng bỏ qua việc thêm).
   - MCP server: tổng quan bản vẽ lớn mất tới khoảng 2 phút lần đầu (có cache); bản vẽ
     MEP (C1, D1) không tự nhận ra mặt bằng; layer có tiền tố `$1$` chưa được làm sạch.
4. **Việc tồn từ trước:** thử tay phần chọn đối tượng mới của HVACSTRETCH (khôi phục từ tag
   `hvacstretch-before-selection` nếu lỗi; rủi ro `(handent ...)` qua
   `SendStringToExecute`); thử HVACREFRIG có sizing; lệnh bóc van, bóc thiết bị qua
   attribute; schema MEP chung; dữ liệu lỗi gán nhãn cho AI.
5. Đề xuất giảm usage cũ (18/11): `model: sonnet` cho agent `research` và
   `CLAUDE_CODE_SUBAGENT_MODEL=sonnet`, người dùng đã dừng; thực tế các lần giao việc vẫn
   dùng agent Sonnet.

## 5. Quyết định đã chốt với người dùng

**HVACQTO**
- Xuất `.xml` (SpreadsheetML). Ống thép `SCH40`. Hệ số dự phòng mặc định 1.0 ở ô F6
  sheet `DIEU HOA THONG GIO`. Thiết lập nhớ giá trị lần trước.
- Ống đồng nhận `a/b` và `RP a-b`, bỏ phần trong ngoặc. Thành ống đồng D12.7–D41.3 dày
  1.0 mm; không đề xuất lại 1.1 mm.
- `CWS`/`CWR` là ống thép. Phụ kiện ống gió có xdata `HVACAI_FITTING` không được bóc.

**Ống gió**
- Phụ kiện là polyline, không bóc khối lượng. Co chữ nhật R tâm = 0.75W.
- Côn: L = max(W_lớn/2, 1.207·ΔW, 1.207·ΔH); côn lệch tâm L = max(W_lớn/2, 1.732·ΔW).
- Chân rẽ: vát 45°, L = max(Wb/2, 102 mm), miệng Wb + L.
- HVACDUCT: chiều dài bằng click hoặc Enter dùng chiều dài trong hộp thoại.
- HVACDUCTALIGN: hai ống song song thì nối luôn, gộp một block; nhánh vuông góc tự thêm
  chân rẽ. HVACSTRETCH: block nằm trọn trong vùng quét thì dời nguyên khối.

**Ống nước, ống ga, dây**
- Bộ chia ga `Branch Connection (1)`, tỉ lệ 1.24, cắt ống ở 3 cổng, layer RP phía vào.
- Quy tắc ống ga: hai bộ chia cách nhau ≥ 1000 mm; từ đầu ra bộ chia tới chỗ gấp ≥ 500 mm
  thẳng; bộ chia đầu tới dàn xa nhất ≤ 40 m, cảnh báo > 37 m.
- Sizing ống ga theo Daikin VRV IV (kW = index × 0.112); nhãn `%%C15.9/9.5`.
- HVACDRAIN: nhỏ nhất D27, nhánh 45° dài 300 mm. HVACPOWERWIRE: móc 45° 300 × 300.
  HVACCTRLWIRE: mỗi dàn một polyline, cách nhau k × 150 mm.

**Tầng 0 và chuỗi phòng → máy → tuyến (21–22/11)**
- Làm tầng 0 trước MCP. Sau khi thấy heuristic tự động không ổn trên bản vẽ thật, chọn
  hướng **bán tự động** (người dùng chọn vùng và tick layer tường trong AutoCAD).
  **Không dùng TraceBoundary.**
- Lõi tìm phòng: polygonize, thay cung cửa bằng dây cung, bịt ô mở giữa hai đầu tường
  ≤ 1800 mm, nối khe ≤ 50 mm, bỏ mặt hẹp < 400 mm. Không dùng máy học cho hình học.
- **Hộp kỹ thuật nhận theo chữ X** (hai nét chéo cắt nhau ở giữa) hoặc chữ HKT/gen/shaft;
  vùng quét cánh cửa không được tính là trục kỹ thuật.
- Layer kết quả: `HVAC-ROOM`, `HVAC-ROOM-TEXT`; nét `HVAC-ROOM-SPLIT` luôn là tường.
- Tải lạnh: **diện tích × W/m² theo loại phòng × hệ số dự phòng**; bảng W/m² do người dùng
  cung cấp. Làm cả 3 tầng (tải, đặt máy, tuyến thô); không thêm cassette 2,2/8,0 kW.
- Thử trên AutoCAD 2021 trong lúc chưa cài 2023.

**Phần AI (22–23/11)**
- Ưu tiên phần AI. Làm theo `docs/AI_MCP_PLAN.md`: Phase 1 rồi Phase 2.
- Host: **VS Code + GitHub Copilot Free** (`.vscode/mcp.json`).
- Bộ đánh giá: **5 bản vẽ trong `CAD/`**.
- Nguyên tắc: AI không chạy trong AutoCAD, không nhận file DWG, mọi thao tác ghi phải qua
  preview và Yes/No, không có tool chạy lệnh tuỳ ý.
- Chế độ bảo mật `normal` / `redact` / `numbers_only`; mức trong cấu hình là mức tối thiểu.

**Git và cách làm việc**
- **Từ 23/11 người dùng tự làm mọi việc commit, push, gộp nhánh. Claude không commit, không
  push**, chỉ báo các file đã đổi (có thể gợi ý commit message). Các commit trước đó trên
  `hvac-room-chain` do Claude tạo khi người dùng còn cho phép. Không commit 5 bản vẽ trong
  `CAD/`.
- Session chính điều phối, được tự tạo agent con (Sonnet). Kế hoạch cho thay đổi lớn vẫn
  phải được duyệt. Agent chạy tuần tự; chỉ chạy song song khi người dùng yêu cầu.
- Sửa trực tiếp trong thư mục chính. Không đụng AutoCAD hay Excel đang mở; `MEP_DWT_3.dwg`
  và các bản vẽ trong `CAD/` chỉ đọc, chỉ xử lý trên bản sao.
- Trả lời bằng tiếng Việt; tên định danh, commit message, comment code bằng tiếng Anh.

## 6. Memory của Claude (để tạo lại trên máy mới)

Memory hiện có trên máy `PC` (thư mục ở mục 1):
1. **machine-pc-environment** (project): máy `PC` có AutoCAD 2021/2024, Python 3.12 + venv
   trong repo, quyền ghi cấp bằng icacls, add-in build được với 2021 và chạy ngầm ổn.
2. **tang0-arch-survey** (project): kết quả khảo sát 5 bản vẽ, các quyết định tầng 0, chuỗi
   lệnh đã làm, các giả định cần hỏi lại (cổng cassette, bảng W/m²).
3. **ai-mcp-integration** (project): đề xuất MCP 21/11, kế hoạch 4 phase, host VS Code +
   Copilot Free, Phase 1 xong, đang chờ quyết định khung chat và Claude API.

4a. **git-user-handles-commits** (feedback, 23/11): người dùng tự commit và push; Claude
   không commit, không push, không đề nghị làm.

Memory từ máy cũ, tạo lại nếu cần:
4. **main-session-delegation** (feedback): session chính điều phối, được tự tạo agent con;
   kế hoạch thay đổi lớn vẫn cần duyệt; trả lời bằng tiếng Việt.
5. **duct-fittings-are-polylines** (project): phụ kiện ống gió là polyline, HVACQTO không bóc.
6. **edit-location** (feedback): sửa trong thư mục chính; gộp việc ở worktree về thư mục chính.
7. **hvac-draw-commands-decisions** (project): quyết định 18–19/11 cho ống gió, ống ga, S1–S4.
8. **sizing-and-wire-commands** (project): quyết định 20/11 cho sizing và dây.

## 7. Kết quả nghiên cứu và đề xuất

### 7.1 Nghiên cứu AI cũ (14/11 và 20/11, theo agent research)
- AI của Autodesk (Assistant, MCP server) chỉ có từ AutoCAD 2027 và gần như chỉ đọc.
- Kiến trúc: Python ngoài AutoCAD, AI chỉ đề xuất, người dùng duyệt, mỗi lần áp dụng là
  một nhóm Undo. Đọc bản vẽ bằng hình học trước; LLM chỉ lo ngữ nghĩa và xếp hạng.
- Gemini API miễn phí thì dữ liệu bị dùng để huấn luyện; Gemini CLI ngừng cho tài khoản cá
  nhân từ 18/06/2026. GitHub Models đóng từ 30/07/2026.

### 7.2 Khảo sát 5 bản vẽ (21/11)
- `P-KT`: kiến trúc thuần, khoảng 20 mặt bằng trong model space, **chữ TCVN3**, tường nằm
  cả trên layer nét (`Net cat`, `A-Netthay`), một đường cắt nét `HIDDEN` xuyên mọi mặt
  bằng, cột tờ cũ bị gạch chéo; nhãn `DT. x M2` là diện tích cả căn hộ/phòng khách sạn.
- `Nhà chống rung`: bộ nhiều tờ, Unicode Việt–Trung, layer chuẩn `CJSC.*`.
- `17.1 LK-E9`, `C1-DHTG`, `D1-DHTG`: bản vẽ MEP, kiến trúc là xref đã bind lồng nhiều lớp
  (D1 khoảng 800 000 đối tượng); C1 phải chạy `AUDIT` trước `DXFOUT`.
- Kết luận: tên layer khác nhau theo từng văn phòng, heuristic tự động hỏng khi đổi bản
  vẽ, nên chọn hướng bán tự động.

### 7.3 Nghiên cứu cách tìm phòng (21/11, agent research, phần lớn là suy luận)
- TraceBoundary của AutoCAD: rẻ nhưng hỏng khi tường hở (người dùng không chọn).
- Polygonize + bịt cửa có chủ đích: làm lõi (đã làm).
- Máy học (FloorPlanCAD, CADTransformer…): bộ dữ liệu FloorPlanCAD giấy phép CC BY-NC,
  không cho đa giác chính xác, cần GPU: không làm.
- AI nhìn ảnh: chỉ hợp để đặt tên/phân loại phòng và gợi ý layer.

### 7.4 Host, giá và bảo mật (22/11, theo claude.com/pricing và tài liệu Claude Code)
| Host | Giá | Ghi chú |
| --- | --- | --- |
| Claude Desktop / Claude Code | Pro $20/tháng ($17 trả năm), Max $100, Ultra $200 | MCP tốt nhất; gói cá nhân phải tắt huấn luyện |
| Claude Team | $25/người/tháng ($20 trả năm), tối thiểu 2 | Không huấn luyện, lưu 30 ngày |
| Claude Enterprise | $20/ghế (năm) + phí API, tối thiểu 20 | Tự đặt thời gian lưu, ZDR nếu được duyệt |
| Claude API | Opus 5.5 $4/$20, Sonnet 5.5 $2/$10, Haiku 5.5 $0,10/$0,50 mỗi 1M token | Ước tính soát một tầng ≈ $0,05 (Sonnet) |
| VS Code + Copilot | Free, Pro $10, Pro+ $39 | **Đã chọn Free**; giới hạn lượt dùng, có thể không đọc ảnh |
| LM Studio (offline) | Miễn phí | Máy `PC` chỉ có GTX 1660 SUPER 6 GB, 16 GB RAM: không đủ; cần khoảng 24 GB VRAM |

- Bảo mật theo 5 mức: tắt huấn luyện (gói cá nhân, lưu 30 ngày) → gói thương mại
  (Team/Enterprise/API) → chạy qua đám mây riêng (Bedrock, Google Cloud, Foundry) →
  **gửi tối thiểu** (đã làm trong Phase 1: chế độ `redact` và `numbers_only`) → offline.
  Đề xuất: Team + gửi tối thiểu; dự án mật dùng `numbers_only`.
- **Khung chat trong AutoCAD** (đề xuất 23/11, chờ quyết định): lệnh `HVACAI` mở khung dock
  (lịch sử chat, ô prompt, chế độ bảo mật, chọn model, chi phí); `ai_agent.py` chạy ngoài
  AutoCAD, gọi Claude API với các tool Phase 1 và Phase 2; đề xuất có nút Xem preview /
  Áp dụng / Bỏ; API key trong Windows Credential Manager; giới hạn chi phí. Ước tính
  Sonnet 5.5: $0,05–0,12 mỗi yêu cầu, $25–50/tháng cho 20 yêu cầu/ngày.

### 7.5 Review repo CAD-MCP (github.com/daobataotie/CAD-MCP, MIT, 23/11)
- Server MCP (mcp 1.x, FastMCP) không gắn host; dùng với Claude Desktop, Cursor, Windsurf,
  TRAE, hoặc HTTP. Điều khiển AutoCAD/GstarCAD/ZWCAD đang mở **qua COM** (`win32com`),
  35 tool vẽ/sửa/truy vấn, `send_command` chạy lệnh tuỳ ý.
- **Nên học:** đọc bản vẽ đang mở qua COM (cho Phase 2, chỉ đọc); luồng COM riêng, tự thử
  lại khi AutoCAD bận (`RPC_E_CALL_REJECTED`), nhóm Undo; đánh dấu tool chỉ đọc/phá huỷ;
  báo tiến độ; đầu ra có cấu trúc; prompt mẫu; kiểm tra server bị chạy sai cách.
- **Không học:** `send_command` và các tool ghi không preview; tool vẽ hình cơ bản; bộ
  phân tích câu lệnh bằng regex; đọc cả bản vẽ lớn qua COM (quá chậm).
- **Dò AutoCAD trong registry:** CAD-MCP liệt kê `HKCR\AutoCAD.Application.xx` rồi chọn bản
  mới nhất. Trên máy `PC` khoá này có cả 24.1 và 24.2 dù không cài 2022/2023 (đăng ký sót),
  nên dễ nối nhầm. Khoá đúng cho thư mục cài là
  `HKLM\SOFTWARE\Autodesk\AutoCAD\R24.x\ACAD-xxxx:409\AcadLocation` (máy `PC`: R24.0 =
  2021, R24.3 = 2024). Đề xuất: dùng khoá này cho `accoreconsole` trong `aitools`; ở
  Phase 2 chỉ nối với phiên AutoCAD đang chạy thật; bỏ qua R25+ (AutoCAD 2025+ dùng
  .NET 8); không áp dụng cho `AcadDir.props`.

## 8. Mẹo môi trường

- AutoCAD giữ DLL đã nạp tới khi thoát; build xong phải khởi động lại AutoCAD.
- Debug: VS Code chính hãng, attach `acad.exe` kiểu `clr`. Antigravity không debug được.
- Startup Suite của APPLOAD không nạp `.dll` ổn định; dùng `.lsp` gọi `NETLOAD` hoặc
  registry demand-load.
- `accoreconsole`: luôn chạy với `/isolate`, trên **bản sao**; với bản vẽ lỗi chạy
  `_.AUDIT` `_Y` trước `_.DXFOUT`; kiểm tra file DXF kết thúc bằng `EOF`; chạy từ PowerShell
  bằng `Start-Process` (Git Bash làm hỏng tham số bắt đầu bằng `/`).
- Console Windows dùng cp1252: Python in tiếng Việt phải `sys.stdout.reconfigure(encoding="utf-8")`;
  `python -I` bỏ qua `PYTHONIOENCODING`. PowerShell bỏ dấu ngoặc kép khi truyền code cho
  `python -c`, nên ghi code ra file script.
- `mcp` 2.x: `FastMCP` đổi thành `MCPServer` (`mcp.server.mcpserver`); lỗi phải ném
  `ToolError` thì model mới thấy nội dung; server stdio không được in ra stdout.
- Usage tính theo tài khoản (cửa sổ 5 giờ và theo tuần), không reset khi mở session mới.
