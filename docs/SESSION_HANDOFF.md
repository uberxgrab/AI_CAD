# Bàn giao các session Claude Code: hvac_ai (AI_CAD)

Cập nhật lúc 28/11/2026 (bổ sung phần ZWCAD, làm trên máy `Admin`). File này tóm tắt các session Claude Code của dự án, để có thể
clone repo về máy khác và làm tiếp. Quy tắc và kiến trúc dự án nằm trong `CLAUDE.md`
và `AGENTS.md`; kế hoạch phần AI nằm trong `docs/AI_MCP_PLAN.md`. File này chỉ ghi phần
không có ở đó: lịch sử, quyết định của người dùng, việc đang dở, và những thứ nằm ngoài
git.

## 1. Làm tiếp trên máy khác

### Trước khi rời máy hiện tại
- Việc đến 24/11 đã lên `origin/main` (`d36fbff`, `f2b6c04`).
- **Máy `Admin` (28/11): `main` đi trước `origin/main` 6 commit, chưa push.** Đó là phần
  ZWCAD, từ `d6c7cad` đến `8f71ca7` (mục 2), cộng commit cập nhật file này. Người dùng tự
  push.
- 5 bản vẽ đánh giá trong `CAD/` **không có trong git** (chưa commit theo ý người dùng).
  Phải chép tay sang máy mới (xem bảng "Những thứ không có trong git").
- Hai tag khôi phục HVACSTRETCH (`hvacstretch-before-s1`, `hvacstretch-before-selection`)
  **có trên máy `Admin` nhưng chưa có trên GitHub**. Push lên: `git push origin --tags`.

### Trên máy mới
1. Clone repo vào **đúng** `C:\Users\Admin\hvac_ai` (quyết định ngày 2026-11-28).
   `.mcp.json`, `start.scr` và `StartArguments` trong `.csproj` gắn cứng đường dẫn này.
   Máy cũ đang để repo ở `C:\Users\Admin\AI_CAD`: sau khi pull, đổi tên thư mục thành
   `C:\Users\Admin\hvac_ai` rồi tạo lại `venv` (bước 4).
   ```powershell
   git clone https://github.com/uberxgrab/AI_CAD.git C:\Users\Admin\hvac_ai
   ```
2. Cài AutoCAD 2021–2024 (add-in build cho .NET Framework 4.8; **AutoCAD 2025 trở lên
   dùng .NET 8 nên không nạp được add-in này**), .NET SDK có targeting pack .NET
   Framework 4.8, và Python 3.12. `cad_addin\AcadDir.props` tự chọn bản đã cài theo thứ
   tự 2023 → 2021 → 2024; muốn chọn bản khác thì thêm `-p:AcadDir="..."`.
3. Nếu repo nằm trong thư mục người dùng khác (máy hiện tại: repo ở `C:\Users\Admin`,
   tài khoản là `pc`) thì phải cấp quyền ghi, chạy PowerShell **Run as Administrator**:
   `icacls "C:\Users\Admin\hvac_ai" /grant "pc:(OI)(CI)M" /T`.
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
6b. **ZWCAD (tuỳ chọn, từ 28/11):** cài ZWCAD 2026 **Professional** (bản Standard không
   nạp được DLL). Máy `Admin` cài ở `D:\ZWCAD`; chỗ khác thì thêm `-p:ZwDir="..."`.
   ```powershell
   dotnet build cad_addin\HvacAiCadAddin.Zw.csproj -p:Configuration=Debug -p:Platform=x64
   powershell -File cad_addin\register-zwcad.ps1     # ZWCAD tự nạp add-in khi khởi động
   ```
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
| Bộ bản vẽ thầu cơ điện (bộ đánh giá cho ống và ống gió) | `C:\Users\PC\Desktop\KLG ĐẠT\2. Mechanical Tender Drawing 20261014\` (M-0 → M-7, kèm các xref) | Chép tay nếu cần thử lại. Bản vẽ của khách hàng: chỉ xử lý trên bản sao, không đưa lên git |
| Bảng ánh xạ dự án (`mep_map`, `HVACNORM`) | `%LOCALAPPDATA%\HvacAiCad\mep_maps\*.json` | Chép nếu đã lưu bảng ánh xạ cho dự án nào; nếu không, lệnh và tool tự đề xuất lại |
| `venv/`, `cad_addin/bin`, `obj` | trong repo, bị `.gitignore` | Tạo lại ở bước 4 và 6 |
| Thiết lập các lệnh | `%APPDATA%\HvacAiCadAddin\*.settings` (HVACQTO, HVACROOMS, HVACLOAD, HVACPLACEUNITS, HVACROUTEDRAFT, HVACPLACEODU, HVACRISER, HVACNORM, HVACDUCTLINE…) | Tự tạo khi chạy lệnh |
| Cấu hình AI riêng máy | `aitools_config.json` (nếu có) | Chép từ `aitools_config.example.json` |
| Cache của MCP server | `%LOCALAPPDATA%\HvacAiCad\work\` (DXF đã chuyển, tổng quan, ảnh, log `mcp_server.log`) | Tự tạo lại |
| File tạm của HVACROOMS / HVACLOAD | `%TEMP%\HvacAiCadAddin\rooms\`, `...\load\` | Tự tạo |
| Mẫu Excel tham khảo | `Desktop\vrv.xlsx`, `Desktop\chiller.xlsx` (máy cũ) | Chỉ là tham khảo |
| Khoá tự nạp add-in cho ZWCAD | `HKCU\Software\ZWSOFT\ZWCAD\2026\en-US\Applications\HvacAiCadAddin` (máy `Admin`) | Chạy `cad_addin\register-zwcad.ps1` |
| Tài liệu .NET của ZWCAD | `D:\ZWCAD\ZWCAD_DotNet_*_2026\*.chm`; bản text đã giải nén ở `C:\scratch\zwdocs\txt_*` | Giải nén lại bằng `hh.exe -decompile` nếu cần tra |
| Log test ZWCAD và test chạy bằng script | `%TEMP%\HvacAiZwTests\<thời gian>\` | Tự tạo |

Agent con đang chạy dở **không** tiếp tục được trên máy khác; giao việc lại từ đầu.

## 2. Trạng thái hiện tại

- Nhánh `main` trên máy `Admin`, đi trước `origin/main` 7 commit chưa push: 6 commit phần
  ZWCAD (`d6c7cad`…`8f71ca7`) và commit cập nhật file này.
- **Chạy trên cả AutoCAD và ZWCAD 2026 (28/11):**
  - Cùng một mã nguồn `cad_addin/src`, viết cho AutoCAD.
  - `cad_addin/HvacAiCadAddin.Zw.csproj` đổi namespace `Autodesk.AutoCAD` →
    `ZwSoft.ZwCAD` khi build (`ZwMap.targets` ghi bản chép vào `obj\Zw\gen`, có `#line` trỏ
    về file gốc) và tham chiếu `ZwManaged.dll`, `ZwDatabaseMgd.dll`.
  - Biên dịch thử: 0 lỗi, không phải đổi tên hàm nào, không có API nào thiếu. Chưa cần
    shim `#if ZWCAD` nào.
  - DLL ZWCAD ở `cad_addin\bin\Zw\Debug\net48\`; ZWCAD trên máy `Admin` đã được đăng ký
    tự nạp bản này.
  - **Chế độ test** (`HVACAI_TEST=1`): dialog tự bấm OK với giá trị mặc định, thiết lập
    đã lưu không bị đọc hay ghi. Không đặt biến thì lệnh chạy như cũ
    (`cad_addin/src/TestMode.cs`, `DialogHost.cs`).
- Các lệnh trong add-in:

| Lệnh | Việc | Đã thử trong AutoCAD? |
| --- | --- | --- |
| `HVACSTRETCH` | Stretch hỗn hợp; từ 20/11 chọn đối tượng như STRETCH gốc | Phần kéo đã ổn. **28/11: người dùng thử tay trên ZWCAD, không có vấn đề** |
| `HVACQTO` | Bóc khối lượng ra `.xml` theo mẫu VRV. Từ 23/11: đếm thêm SPLINE/ARC; đọc tag kích thước của HVACNORM; ống khác vật liệu ra dòng "`<mã hệ> - <mô tả>`"; ống gió nét đơn đã gắn tag được tính diện tích; ống đứng có thêm loại PPR/HDPE/uPVC thoát nước | Bản có công thức chưa ghi nhận đã thử; phần mở rộng 23/11 chưa thử tay |
| **`HVACNORM`** (23/11) | Chuẩn hoá ống và ống gió nét đơn của bản vẽ tư vấn khác: Python `mep_normalize.py` đọc nhãn, hộp thoại *layer → mã hệ*, preview, Yes. Xoá nét trùng, đưa thuộc tính về ByLayer, cắt nét tại chỗ đổi kích thước, chuyển ống trùng vật liệu BOQ sang layer `EIA_1` có sẵn, ghi tag `HVACAI_PIPE`/`HVACAI_DUCT` cho phần còn lại | Chưa (đã chạy không giao diện trên bản sao M-1-07~10 và M-4-12~14) |
| **`HVACDUCTLINE`** (23–24/11) | Vẽ ống gió (block `REC_`/`Round D` kèm cút, côn, tap) từ nét đơn: chọn nét của một hệ, bấm đầu quạt hoặc AHU | Chưa (đã chạy không giao diện trên bản sao M-4-12~14: 89/89 tuyến; M-4-10~11: 258/296 tuyến) |
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
- Luồng cho bản vẽ của tư vấn khác (23–24/11): `mep_map` (trong Copilot) hoặc `HVACNORM`
  (trong AutoCAD) → `HVACDUCTLINE` cho ống gió nét đơn → `HVACQTO`.
- **Phần AI, Phase 1 đã xong (23/11):** `mcp_server.py` + gói `aitools/`.
  - Có 9 tool; tool thứ 9 là **`mep_map`** (23/11).
  - Chưa chạy thử trong Copilot. Ngày 24/11 người dùng hỏi cách cho Copilot tự duyệt tool; câu trả lời ở mục 8.
  - Các lệnh trong AutoCAD chưa được bọc thành tool cho host; hai cách làm ở mục 4.1.
- Test tự động (24/11, tất cả qua):
  - 69 test Python (`discover -s tests`).
  - C#: `RoomsCoreTests` 285, `LoadCoreTests` 168, `UnitCoreTests` 492, `RouteCoreTests` 45 978, `OduRiserCoreTests` 537, `PipeCoreTests` 20 982, `DuctCoreTests` 2 523, `NormCoreTests` 243, `HvacQtoCoreTests` 19 311.
  - Chạy không giao diện trong `accoreconsole`: `tests/DuctAcadTests`, `tests/UnitCoreTests/Acad`, `tests/OduRiserCoreTests/Acad/run-tests.ps1`, và `tests/NormCoreTests/Acad/run-tests.ps1`. Script cuối đạt 37/37 trên bản vẽ trống, cả AutoCAD 2021 lẫn 2024; có thêm `-RealDwg <dwg>` và `-Duct` để chạy trên bản sao bản vẽ thật.
- Test ngày 28/11 (máy `Admin`, AutoCAD 2023 + ZWCAD 2026):
  - `tests\run-core-tests.ps1` (9 project C# lõi): 90 519 qua, 0 lỗi.
  - `tests\Zw\run-zw-tests.ps1 -Suite all`: 708 qua, 0 lỗi.
    - smoke 16, duct 516, unit 28, oduriser 33, norm 37, scripted 78.
    - Mỗi suite chạy trong một phiên ZWCAD riêng; script chỉ tắt đúng tiến trình nó mở.
  - **Test lệnh bằng script** (`tests/Scripted/`): 13 tình huống, file `.scr` nhập sẵn
    toạ độ, xuất bản vẽ ra JSON, so với `expected/` và so AutoCAD với ZWCAD.
    - Các tình huống: HVACSTRETCH ×4, HVACDUCT, HVACDUCTALIGN ×2, HVACPIPESIZE,
      HVACREFRIG, HVACDRAIN, HVACPOWERWIRE, HVACCTRLWIRE, HVACQTO.
    - ZWCAD: 78 qua, 9 lỗi đã biết (đều `stretch_*`, ghi trong `known-failures.json`).
    - AutoCAD (`run-acad-scripted.ps1`, `accoreconsole /isolate`): 85 qua.
    - 9 tình huống không phải stretch cho kết quả như nhau trên hai bên (lệch dưới 0,01 mm).

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
| 23/11 chiều–24/11 | `54890397` (tiếp, sau khi compact) | Khảo sát bộ bản vẽ thầu cơ điện (M-1 cấp thoát nước, M-4 ống gió). Bước 1: `mep_normalize.py` và tool `mep_map`. Bước 2: lệnh `HVACNORM` và phần mở rộng `HVACQTO`. Bước 3: lệnh `HVACDUCTLINE`. Trả lời câu hỏi bọc lệnh thành tool cho host, và cách cho Copilot tự duyệt tool. Xem mục 7.6. |
| 28/11 | `f196b7b5` (máy `Admin`, session chính cũ, tiếp sau khi compact) | **Chạy add-in trên ZWCAD 2026.** Đọc và tóm tắt 3 tài liệu .NET của ZWCAD (mục 7.7). Biên dịch thử bản sao với DLL ZWCAD: chỉ cần đổi namespace. Bước 1: project ZWCAD và script chạy test lõi. Bước 2: test nạp DLL trong ZWCAD. Bước 3: chạy lại test ghi bản vẽ trong ZWCAD. Bước 4: chế độ test bỏ qua dialog và 13 tình huống test lệnh bằng script trên cả hai CAD. Đăng ký ZWCAD tự nạp add-in. |

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
7. **Chọn cách bọc `HVACNORM`, `HVACDUCTLINE`, `HVACQTO` thành tool cho host** (hỏi 24/11,
   chưa trả lời):
   - **Cách A, Claude đề xuất làm trước** (khoảng 1 session):
     - Thêm 1 tool, `process_drawing(drawing, map_name, steps, boq)`.
     - Tool chạy accoreconsole trên **bản sao**, áp HVACNORM, vẽ ống gió, và xuất file BOQ `.xml`.
     - Điểm gốc của mỗi mạng ống gió được tự đoán: đầu tự do của ống lớn nhất.
   - **Cách B, Phase 2** (khoảng 2 session):
     - Cầu nối named pipe tới bản vẽ đang mở, khoảng 3 tool.
     - Preview và Yes/No vẫn ở trong AutoCAD; người dùng tự chọn điểm gốc ống gió.
8. **Duyệt tên hạng mục BOQ đã đề xuất** cho ống khác vật liệu: PPR PN10, hoặc PN20 cho
   nước nóng; HDPE PE100 PN10; uPVC Class 2; inox SUS; thép; đồng. Cấp áp lực hiện là
   hằng số trong `TakeoffClassifier.DescribePlumbing`, chưa sửa được trong hộp thoại.

### 4.2 Việc kỹ thuật
1. **Thử tay** `HVACPLACEODU`, `HVACRISER`, `HVACROUTEDRAFT` bản cổng + hành lang (các
   bước đã gửi người dùng 22/11), và thử Phase 1 trong Copilot (câu hỏi mẫu: "liệt kê bản
   vẽ, cho tôi các mặt bằng của P-KT, thống kê layer tầng 9 và gợi ý layer tường, rồi tìm
   phòng và tính tải lạnh").
2. **Thử tay ba lệnh mới**, các bước đã gửi người dùng trong chat; dùng **bản sao** của bản vẽ thầu:
   - `HVACNORM` trên M-1-07~10; kiểm tra Undo về đúng một bước;
   - `HVACDUCTLINE` trên M-4-12~14, chọn một mạng EA rồi bấm đầu quạt;
   - `HVACQTO` trên cùng vùng đó, kiểm tra nhóm "Ống cấp thoát nước và ống khác" và phần ống gió tính từ block.
   - Thử `mep_map` trong Copilot.
2b. **Commit và push: người dùng tự làm.** Các file chưa commit của ngày 23–24/11:
   - Mới:
     - Python: `mep_normalize.py`, `aitools/mep.py`, `tests/test_mep_normalize.py`;
     - `cad_addin/src/`: `HvacNorm.cs`, `NormCore.cs`, `NormExchange.cs`, `NormForm.cs`, `NormWriter.cs`, `SizeTagCore.cs`, `HvacDuctLine.cs`, `DuctLineCore.cs`, `DuctLineWriter.cs`, `DuctLineForm.cs`;
     - test C#: `tests/NormCoreTests/` (cả `Acad/`), `tests/HvacQtoCoreTests/TagTests.cs`, `tests/DuctCoreTests/DuctLineTests.cs`.
   - Sửa:
     - code: `HvacQuantityTakeoff.cs`, `HvacQuantityTakeoffCore.cs`, `mcp_server.py`;
     - test: `tests/test_aitools.py`; `FormulaTests.cs`, `Program.cs`, `.csproj` của `HvacQtoCoreTests`; `Program.cs`, `.csproj` của `DuctCoreTests`;
     - tài liệu: `CLAUDE.md`, `docs/AI_MCP_PLAN.md`, `docs/SESSION_HANDOFF.md`.
   - Commit message gợi ý: `Add mep_map, HVACNORM and HVACDUCTLINE; size tags in HVACQTO`.
2c. **Giới hạn của chuỗi mới, chưa sửa:**
   - `mep_map`/`HVACNORM`: các nhánh lặp kiểu "điển hình" không có nhãn thì không đoán
     được kích thước. Mức "thấp" (lấy kích thước qua chỗ rẽ nhánh) có thể gán cỡ ống chính
     cho nhánh nhỏ, nên người dùng phải soát preview. Dải chú giải layer 3 m vẫn bị
     `HVACQTO` đếm nếu quét chọn trúng.
   - `HVACDUCTLINE`:
     - nhánh quá ngắn so với tap cộng cút, hoặc chỗ vòng gấp hơn 170°, thì không vẽ (giữ nét đơn, báo đỏ);
     - ở T chia đôi (bullhead), nhánh còn lại không có tap;
     - nhánh xiên không có tap;
     - tap có thể chồng lên côn khi ống chính đổi cỡ ngay tại chữ T;
     - chiều tap luôn hướng về phía gốc.
   - Ống gió nét đơn chỉ được gắn tag, **không** đổi layer, để khỏi đếm hai lần khi
     `HVACDUCTLINE` vẽ block. Đây là quyết định của Claude, đã báo người dùng.
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
3b. **ZWCAD, còn lại sau 28/11:**
   - Thử tay trên ZWCAD các lệnh có preview, dialog hoặc block động: HVACDUCT,
     HVACDUCTALIGN, HVACREFRIG, HVACDRAIN, HVACPOWERWIRE, HVACCTRLWIRE, chuỗi HVACROOMS →
     HVACRISER, HVACNORM, HVACDUCTLINE. HVACSTRETCH đã thử, không có vấn đề.
   - Chưa có tình huống test bằng script cho HVACPLACEUNITS, HVACPLACEODU, HVACRISER,
     HVACROUTEDRAFT, HVACROOMS, HVACLOAD và HVACNORM. Các lệnh này cần dữ liệu phòng và tải
     lạnh tạo giả. `HVACAI_TEST_SETTINGS` đã làm nhưng chưa tình huống nào dùng.
   - **Lỗi đã biết, chỉ khi chạy bằng script trên ZWCAD:** khi một lệnh đang chạy, ZWCAD bỏ
     dấu xuống dòng đầu tiên của chuỗi `SendStringToExecute`, nên HVACSTRETCH gửi thành lệnh
     lạ `_.STRETCH_C`. Ngoài ra `PointMonitor` đọc vị trí chuột, không đọc điểm nhập từ
     script. Người dùng thử tay thì chạy đúng, nên chưa thêm shim.
   - Phần Python (`drawings.py`, MCP tool `convert_dwg`) vẫn dùng `accoreconsole` của
     AutoCAD để đổi DWG sang DXF. ZWCAD không có console chạy ngầm.
   - Sau test `norm`, ZWCAD không tự thoát; script tắt đúng tiến trình đó 25 giây sau khi
     log xong. Chưa rõ nguyên nhân.
4. **Việc tồn từ trước:** thử HVACREFRIG có sizing; lệnh bóc van, bóc thiết bị qua
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

**Chuẩn hoá ống, ống gió và lập lại BOQ (23/11)**
- **Ít tool:**
  - Phía host chỉ có `mep_map`.
  - Phía AutoCAD có hai lệnh `HVACNORM` và `HVACDUCTLINE`.
  - BOQ vẫn lập bằng `HVACQTO` (mở rộng), không thêm tool BOQ riêng.
- **AI nhận diện layer:** AI nhận diện layer của tư vấn khác và đưa về chuẩn template. Kích thước còn thiếu được đoán từ chữ gần ống.
- **Không tạo layer mới:**
  - Ống có vật liệu trùng BOQ (CHW thép, nước ngưng uPVC, ống ga đồng) chuyển sang layer `EIA_1` **có sẵn**, nạp từ template nếu bản vẽ chưa có.
  - Ống vật liệu khác giữ layer cũ. Trong BOQ, dòng của nó có **tiền tố** = mã hệ trong nhãn (hoặc tên layer cũ), dạng `WS - Ống PPR PN10 D90`.
  - Tên hạng mục theo vật liệu do Claude đề xuất (người dùng giao "đề xuất thêm").
- Màu, kiểu nét hoặc độ dày nét bị ghi đè (HIDDEN, màu riêng) là **lỗi ghi đè**, được đưa về ByLayer.
- Chiều cao ống đứng: **người dùng nhập** trong hộp thoại ống đứng của `HVACQTO`.
- Ống gió: đọc ống gió nét đơn, và có lệnh vẽ ống gió từ nét đơn giống `HVACREFRIG`/`HVACDRAIN`.

**AutoCAD và ZWCAD (28/11)**
- **Từ 28/11, mọi kế hoạch xây dựng hay sửa lệnh phải khả thi cho cả AutoCAD và ZWCAD
  2026.** Mỗi kế hoạch có một mục riêng cho hai phần mềm: API cần dùng và ZWCAD có không,
  khác biệt đã biết, cách test trên từng bên, shim `#if ZWCAD` nếu cần. Build cả hai bản
  trước khi báo xong.
- Một mã nguồn: `cad_addin/src` viết cho AutoCAD; bản ZWCAD đổi namespace lúc build. Chỉ
  dùng shim `#if ZWCAD` cho chỗ khác biệt thật.
- Test trên ZWCAD được mở một phiên ZWCAD riêng (cùng profile của người dùng), không đụng
  phiên đang mở, chỉ tắt đúng PID của mình. Không đổi cài đặt bảo mật hay biến hệ thống
  lâu dài (`OSMODE` tắt tạm trong script rồi trả lại).
- Được tạo khoá `HKCU\...\ZWSOFT\ZWCAD\2026\en-US\Applications\HvacAiCadAddin` (đã tạo).
  Gỡ bằng `register-zwcad.ps1 -Unregister` trước khi chạy `run-zw-tests.ps1`, nếu không
  add-in bị nạp hai lần.
- Được thêm chế độ test bỏ qua dialog (`HVACAI_TEST=1`).
- Claude commit, người dùng tự push (xem "Git và cách làm việc").

**Git và cách làm việc**
- **Từ 23/11 người dùng tự làm mọi việc commit, push, gộp nhánh. Claude không commit, không
  push**, chỉ báo các file đã đổi (có thể gợi ý commit message). Các commit trước đó trên
  `hvac-room-chain` do Claude tạo khi người dùng còn cho phép. Không commit 5 bản vẽ trong
  `CAD/`.
  - **Đổi ngày 28/11 (người dùng chốt, áp dụng cho mọi máy): Claude được commit, người
    dùng chỉ tự push.** Claude commit việc đã xong lên `main` (commit gọn, kèm dòng
    Co-Authored-By), không bao giờ push, kể cả tag. Quy tắc 23/11 ở trên không còn hiệu lực.
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

4a. **git-user-handles-commits** (feedback, sửa 28/11): Claude commit lên `main`, người
   dùng tự push; Claude không push.
4b. **pipe-audit-boq-tool** (project, 23–24/11): yêu cầu kiểm tra ống theo quy tắc add-in và
   lập lại BOQ; vị trí bộ bản vẽ thầu; các quyết định ở mục 5; kết quả khảo sát ở mục 7.6;
   trạng thái 3 bước (đều xong, chờ thử tay và commit).

Memory từ máy cũ, tạo lại nếu cần:
4. **main-session-delegation** (feedback): session chính điều phối, được tự tạo agent con;
   kế hoạch thay đổi lớn vẫn cần duyệt; trả lời bằng tiếng Việt.
5. **duct-fittings-are-polylines** (project): phụ kiện ống gió là polyline, HVACQTO không bóc.
6. **edit-location** (feedback): sửa trong thư mục chính; gộp việc ở worktree về thư mục chính.
7. **hvac-draw-commands-decisions** (project): quyết định 18–19/11 cho ống gió, ống ga, S1–S4.
8. **sizing-and-wire-commands** (project): quyết định 20/11 cho sizing và dây.

Memory mới trên máy `Admin` (28/11), tạo lại trên máy khác:
9. **zwcad-port** (project): ZWCAD 2026 Professional ở `D:\ZWCAD`; cách build hai bản;
   các bước 1–4 đã xong và kết quả test; lỗi `SendStringToExecute` chỉ khi chạy bằng
   script; khoá tự nạp đã tạo; người dùng tự push.
10. **plan-for-autocad-and-zwcad** (feedback): mọi kế hoạch phải khả thi và được test trên
    cả AutoCAD và ZWCAD (mục 5).

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

### 7.6 Bộ bản vẽ thầu cơ điện (23–24/11)

Thư mục ở mục 1. Bộ này của tư vấn khác: chuẩn layer `M-P-*`, `M-HVAC-*`, `0-M-*`; mặt
bằng nằm trong xref.

**M-1-01 đến M-1-11 (cấp thoát nước, bếp)**

| Bản vẽ | Nội dung | Kết quả (tính cả mức đoán) |
| --- | --- | --- |
| M-1-01, M-1-02 | Sơ đồ nguyên lý (tên tờ trong paper space) | Tool cảnh báo, không tính BOQ |
| M-1-03~06 | Mặt bằng cấp nước | 99% chiều dài có kích thước |
| M-1-07~10 | Mặt bằng thoát nước | 85% chiều dài có kích thước |
| M-1-11 | Thiết bị bếp | Gần như không có ống |

- Kích thước chỉ nằm trong chữ (`WS-PPR-DN90`, `WW-UPVC-DN110, I=1%`). Chữ cao 600 mm, hệ
  số độ rộng 0,75, đặt ngay sau đầu ống, nên phải đo khoảng cách từ **khung chữ** chứ không
  từ tâm chữ.
- Lỗi tìm được ở mặt bằng thoát nước:
  - 59 SPLINE, khoảng 51 m;
  - 45 m nét vẽ hai lần;
  - 111 nét ghi đè kiểu nét HIDDEN;
  - các khe hở 10–150 mm.
- Mỗi layout có một dải chú giải layer, mỗi layer một nét 3 m. Khung tên vẽ trên layer `0`
  trong model space.

**M-4-10~11 (ống gió AHU) và M-4-12~14 (thông gió)**
- Ống gió vẽ **nét đơn** (đường tâm) trên `M-HVAC-DUCT-SA/RA/OA/EA`:
  - góc là cung tròn; có cung "vượt" ở chỗ hai ống bắt chéo;
  - kích thước nằm trong MULTILEADER, ví dụ `SAD-1200x800 / RAD-1200x700` hay `EAD-150ø`;
  - số miệng gió ghi kiểu `105x(SAG WITH HEPA-600x600)`.
- Ống gió tươi (`OAD`) nằm trên layer SA, nên nhãn bị gán theo hệ ghi trên nhãn và báo lỗi
  `system_mismatch`.
- Các nhánh lặp kiểu "điển hình" không có nhãn, nên M-4-10~11 chỉ có 69% chiều dài có kích
  thước. M-4-12~14 đạt 85%.
- Kết quả `HVACDUCTLINE` (bảng dưới) đều chạy không giao diện trên bản sao, không có lỗi ghi.

  | Bản vẽ | Số mạng | Tuyến vẽ được |
  | --- | --- | --- |
  | M-4-12~14 | 48 | 89/89 |
  | M-4-10~11 | 37 | 258/296 |

**Bài học khi đọc nhãn**
- Cắt nét tại chữ T, và cắt ở giữa hai nhãn khác kích thước trên cùng một đoạn (chỗ đổi
  tiết diện mà nét đơn không vẽ).
- Coi tuyến đi thẳng qua chữ T là cùng một tuyến.
- Phát hiện nét trùng trước khi gán nhãn.
- Nếu không có ống đúng hệ thì cho nhãn gán sang ống cùng loại ở gần (lượt nới lỏng).
- Đọc tên tờ trong paper space để nhận ra sơ đồ nguyên lý.

### 7.7 Tài liệu .NET của ZWCAD 2026 (28/11)
- **Migration Guide:**
  - Thay `AcCoreMgd`/`AcDbMgd`/`AcMgd` bằng `ZwManaged.dll` + `ZwDatabaseMgd.dll`.
  - Đổi `Autodesk.AutoCAD` → `ZwSoft.ZwCAD`, `Acad` → `Zcad`.
  - API .NET của ZWCAD 2025–2026 tương thích AutoCAD 2024, chạy .NET Framework 4.7 (DLL
    build 4.8 vẫn nạp được, đã kiểm). Không còn bản 32-bit.
  - Registry: `HKCU\Software\ZWSOFT\ZWCAD\2026\<ngôn ngữ>`. ZWCAD không có `ACADVER`, dùng
    `VERNUM`. File mặc định đổi tên: `zwcad.lin`, `zwcad.pat`, `zwcad.pgp`, `ZWCAD2026.lsp`.
  - Tự nạp DLL: registry `...\Applications\<tên>` với `MANAGED=1`, `LOADER`, `LOADCTRLS`,
    hoặc `ZWCAD2026.lsp`, Startup Suite, `zwcad.exe /b script.scr`. Không hỗ trợ `.bundle`.
- **Developer Guide:** hướng dẫn theo chủ đề (database, entity, xdata, selection, jig,
  dialog, ribbon, sự kiện, overrule, COM).
  - Bản **Standard không nạp được DLL**.
  - Không có console chạy ngầm như `accoreconsole`.
  - Breakpoint trong dialog hoặc jig chỉ dừng khi chạy lệnh qua `NETDEBUG`.
- **Reference** (`ZRX_Mgd.chm`, khoảng 3 400 trang): tra API theo lớp. `TransientManager`
  không có trang nào nhưng có trong DLL và chạy được.
- **Khác biệt đo được khi test:**
  - Mở template như một bản vẽ thì ZWCAD hiện hộp thoại "Missing SHX Files", chặn script.
    Test đọc template vào database chạy ngầm.
  - Lỗi `SendStringToExecute` mất dấu xuống dòng khi chạy bằng script (mục 4.2).
  - ZWCAD vẫn bắt điểm (osnap) vào toạ độ nhập tay.
  - `ZWCAD.exe` trả mã 1 dù thoát bình thường.
  - `Utils.IsCommandDefined` luôn trả False.
  - Tên DXF của bảng vẫn là `ACAD_TABLE`.

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
- Copilot tự duyệt tool (24/11):
  - Ở hộp xác nhận, bấm mũi tên cạnh **Allow** rồi chọn **Allow in this Workspace** cho từng tool `hvac-cad`. Đây là cách khuyên dùng.
  - Setting `chat.tools.global.autoApprove` hoặc chế độ **Autopilot** duyệt **mọi** tool, kể cả sửa file và terminal, nên không khuyên dùng với bản vẽ khách hàng.
  - Lệnh "Reset Tool Confirmations" xoá các quyền đã cho.
- Script Python sinh code C#: ký tự `\n` trong chuỗi Python dễ bị biến thành xuống dòng
  thật; sửa file `.cs` bằng công cụ Edit thay cho script.
- ZWCAD (28/11):
  - ZWCAD cũng giữ DLL đã nạp tới khi thoát; tắt ZWCAD trước khi build lại bản Zw.
  - Debug: attach `ZWCAD.exe` kiểu `clr`. Breakpoint trong dialog thì chạy lệnh qua
    `NETDEBUG`. Breakpoint đặt trong `cad_addin/src` phải khớp nhờ `#line` (chưa thử).
  - Chạy test: `powershell -File tests\Zw\run-zw-tests.ps1 -Suite all` (hoặc `smoke`,
    `writers`, `scripted`…), `powershell -File tests\Scripted\run-acad-scripted.ps1`,
    `powershell -File tests\run-core-tests.ps1`. Log ở `%TEMP%\HvacAiZwTests\`.
- `mep_map` đọc được bản vẽ ngoài `CAD/` nếu đưa đường dẫn đầy đủ; muốn `list_drawings`
  liệt kê thì thêm thư mục vào `drawing_dirs` trong `aitools_config.json`.
