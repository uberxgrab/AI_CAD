# Kế hoạch phần AI (4 phase)

Lập ngày 22/11/2026, dựa trên đề xuất MCP ngày 21/11 (memory `ai-mcp-integration`) và chuỗi
lệnh đã có: `HVACROOMS` → `HVACLOAD` → `HVACPLACEUNITS` → `HVACROUTEDRAFT` →
`HVACREFRIG` / `HVACDRAIN` / `HVACPOWERWIRE` → `HVACQTO`. Chưa phase nào được làm.

## Nguyên tắc (giữ ở mọi phase)

- **AI nằm trên các lệnh đã có, không viết lại.** Hình học vẫn do code tất định (tầng 0,
  polygonize, các bộ làm sạch ống). AI chỉ chọn, gợi ý, kiểm tra và đặt tên.
- **AI không chạy trong AutoCAD.** Model chạy ở host (Claude Desktop / Claude Code, hoặc
  LM Studio khi cần offline). Add-in chỉ nói chuyện qua file hoặc named pipe.
- **Mọi thao tác ghi vào bản vẽ đều qua preview và Yes/No trong AutoCAD**, một nhóm Undo.
  Không có tool chạy LISP hay lệnh tuỳ ý.
- **Mỗi lần người dùng xác nhận được lưu lại làm dữ liệu** để đo và cải thiện về sau.

```
Claude Desktop / Claude Code (model)
        │  MCP (stdio)
        ▼
mcp_server.py (Python, venv) ── file tools ──► arch_rooms.py, heat_load.py, render PNG, accoreconsole
        │  named pipe \\.\pipe\HvacAiCad (phase 2)
        ▼
Add-in (AutoCAD) ── chạy Core/Writer có sẵn khi CMDACTIVE = 0 ── preview + Yes/No
```

## Trạng thái (23/11/2026)

- Người dùng chọn host **VS Code + GitHub Copilot Free**, bắt đầu Phase 1 rồi Phase 2, và
  dùng 5 bản vẽ trong `CAD/` làm bộ đánh giá.
- **Phase 1 đã xong**: `mcp_server.py` + `aitools/`, 46 test Python qua, chạy được trên cả 5
  bản vẽ (chuyển DWG 2–13 s; tổng quan 8–118 s, có lưu đệm; thống kê layer 15–23 KB JSON).
  Chưa chạy thử trong Copilot.
- **Đang chờ người dùng quyết định**: có làm khung chat ngay trong AutoCAD (lệnh `HVACAI`,
  khung dock, gọi Claude API trả theo lượng dùng qua một tiến trình Python ngoài AutoCAD,
  đề xuất có nút Xem/Áp dụng/Bỏ) trong Phase 2 không, và có tạo tài khoản Claude API với
  giới hạn chi phí không. Copilot Free không có API cho ứng dụng tự viết.

## Phase 1 — MCP server chỉ đọc file (không sửa add-in)

**Mục tiêu:** hỏi được AI về một bản vẽ: "bản vẽ này có những mặt bằng nào, layer nào là tường,
tầng 9 có bao nhiêu phòng, tải lạnh bao nhiêu".

- `mcp_server.py` (FastMCP, gói `mcp` cài vào `venv`) với các tool:
  - `convert_dwg(path)`: chuyển **bản sao** DWG sang DXF bằng `accoreconsole` (có `/isolate`).
  - `drawing_overview(dxf)`: các mặt bằng tìm được (tái dùng `arch_extract`), tiêu đề, tầng.
  - `layer_profile(dxf, region)`: thống kê từng layer: số đối tượng, tổng chiều dài, kiểu nét,
    khoảng cách cặp nét song song phổ biến (dấu hiệu tường), chữ mẫu. Đây là đầu vào chính để
    AI đoán vai trò layer.
  - `render_region(dxf, region, layers, overlay)`: ảnh PNG một vùng (matplotlib), có thể phủ
    phòng tìm được, để model có thị giác nhìn.
  - `find_rooms(dxf, region, wall_layers)`: gọi `arch_rooms.run_request`.
  - `cooling_load(rooms, table)`: gọi `heat_load.py`.
- Cấu hình host: `claude_desktop_config.json` (Claude Desktop) và `.mcp.json` (Claude Code).
- **Kiểm tra:** test Python gọi trực tiếp từng tool trên DXF tự sinh và bản sao `P-KT`; thử
  trong Claude Desktop với câu hỏi mẫu.
- **Xong khi:** trên `P-KT`, AI tự gợi ý được bộ layer tường gần với bộ bạn đã tick tay, và chạy
  ra phòng, tải lạnh mà không cần mở AutoCAD.
- **Khối lượng:** nhỏ, khoảng một session.

## Phase 2 — Cầu nối add-in (AI thao tác trên bản vẽ đang mở)

**Mục tiêu:** AI làm việc trên bản vẽ đang mở trong AutoCAD qua các lệnh có sẵn, bạn duyệt từng
bước.

- Trong add-in: lệnh `HVACAIBRIDGE` bật một named pipe server. Yêu cầu được xếp hàng và chạy
  trên luồng chính của AutoCAD khi không có lệnh nào đang chạy (`CMDACTIVE = 0`).
- Tool đọc: thông tin bản vẽ, vùng chọn hiện tại (xuất snapshot như `HVACROOMS`), danh sách
  phòng (`HVACAI_ROOM` / `HVACAI_LOAD`), dàn lạnh (`HVACAI_PLACED`), dàn nóng, ống đứng.
- Tool ghi dạng **đề xuất**: `propose_rooms`, `propose_loads`, `propose_units`,
  `propose_routes`. Add-in hiện preview bằng transient, bạn chọn Yes/No ngay trong AutoCAD, rồi
  dùng lại các Writer có sẵn (`RoomsWriter`, `LoadWriter`, `UnitPlacementWriter`,
  `RouteDraftWriter`). Mỗi đề xuất là một nhóm Undo.
- Mọi yêu cầu đều được ghi log (thời gian, tool, kết quả, bạn chọn Yes hay No).
- **Kiểm tra:** test C# cho giao thức thông điệp (không cần AutoCAD), test Python với server
  giả, và thử tay trong AutoCAD.
- **Xong khi:** từ Claude Desktop bạn nói "tìm phòng tầng đang chọn, tính tải, đặt dàn lạnh",
  AI gọi lần lượt các bước, mỗi bước bạn bấm Yes trong AutoCAD.
- **Khối lượng:** vừa đến lớn, khoảng hai session.

## Phase 3 — AI hỗ trợ nhận dạng và vòng dữ liệu

**Mục tiêu:** giảm các lỗi còn lại của tầng 0 và `HVACLOAD`, và đo được mức cải thiện.

- **Vai trò layer:** model đọc `layer_profile` và ảnh, đề xuất layer nào là tường, cửa, kính,
  mặt tiền, nội thất, rồi tick sẵn trong hộp thoại `HVACROOMS`. Bạn xác nhận.
- **Soát kết quả bằng ảnh:** phủ phòng lên ảnh mặt bằng. Model chỉ ra phòng bị rò, phòng bị gộp,
  hộp kỹ thuật bị thiếu, và đề xuất nét `HVAC-ROOM-SPLIT` hoặc thêm layer rồi chạy lại.
- **Tên và loại phòng:** model sửa tên nhặt nhầm (ví dụ mã cửa "r2650c1700- dạ1000") và phân loại
  phòng cho `HVACLOAD` từ tên, diện tích và chữ xung quanh. Bạn duyệt trong bảng.
- **Luật tất định mới:** một nét nối hai đầu tường được coi là ô mở (đã ghi ở đề xuất 21/11).
- **Vòng dữ liệu:** mỗi lần bạn xác nhận được lưu thành JSONL gồm vai trò layer, các chỉnh sửa
  và phòng cuối cùng. Dựng bộ đánh giá 3–5 bản vẽ có đáp án để so "chỉ luật" với "luật + AI".
  Khi đủ dữ liệu, huấn luyện một bộ phân loại vai trò layer nhỏ chạy offline (scikit-learn đã có
  trong venv), để bớt phụ thuộc model đám mây.
- **Xong khi:** trên bộ đánh giá, tỉ lệ phòng đúng tăng rõ so với chỉ luật, và số lần bạn phải
  sửa tay giảm.
- **Khối lượng:** lớn. Cần bạn cung cấp và xác nhận đáp án cho vài bản vẽ.

## Phase 4 — Ống gió và tối ưu tuyến

**Mục tiêu:** mở rộng sang ống gió và để AI chọn phương án tuyến tốt nhất.

- **Ống gió:** với FCU nối ống gió hoặc AHU, AI đề xuất tuyến ống gió chính theo hành lang từ
  hình học phòng. Python tính kích thước theo phương pháp ma sát đều. Việc vẽ dùng lõi
  `HVACDUCT` có sẵn (`DuctRouteSession`, phụ kiện theo SMACNA) qua cầu nối.
- **Tối ưu tuyến:** thuật toán sinh vài phương án tuyến ống ga, nước ngưng và dây điện, gồm cả
  tuyến đi theo hành lang. AI xếp hạng theo quy tắc (khoảng cách bộ chia, chiều dài tối đa
  40 m, số lần cắt phòng) và giải thích lựa chọn. Bạn chọn.
- **Kiểm tra cuối:** chạy bộ luật kiểm tra bản vẽ (pipeline `rules.py`) trên kết quả, rồi
  `HVACQTO` để ra bảng khối lượng.
- **Khối lượng:** lớn.

## Cần bạn quyết định trước khi bắt đầu

1. **Host:** Claude Desktop (dễ dùng nhất), Claude Code, hay LM Studio (offline, model nhỏ hơn).
2. **Dữ liệu bản vẽ gửi lên AI đám mây:** dùng Claude Desktop hay Claude Code thì ảnh và thống kê
   bản vẽ của khách hàng được gửi đến nhà cung cấp model. Có dự án nào cần giữ kín, phải chạy
   offline không?
3. **Làm phase nào trước.** Đề xuất: Phase 1, rồi Phase 2, vì Phase 3 và 4 cần cả hai.
4. **Bộ đánh giá cho Phase 3:** 3–5 bản vẽ mà bạn có thể xác nhận đáp án phòng.
