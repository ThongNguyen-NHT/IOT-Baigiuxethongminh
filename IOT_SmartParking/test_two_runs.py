import urllib.request, urllib.error, json, time

def run_scenarios(run_label):
    print(f"\n==================== {run_label} ====================")
    all_passed = True
    for i in range(1, 13):
        req = urllib.request.Request(
            f'http://localhost:5137/api/demo/scenarios/{i}/run',
            data=b'',
            headers={'Content-Type': 'application/json'}
        )
        try:
            resp = urllib.request.urlopen(req)
            data = json.loads(resp.read().decode('utf-8'))
            passed = data.get('passed', False)
            print(f"[{'PASS' if passed else 'FAIL'}] Kịch bản {i}: {data['name']}")
            print(f"    Trước:   {data.get('beforeState')}")
            print(f"    Sau:     {data.get('afterState')}")
            print(f"    Thực tế: {data.get('actual')}")
            if not passed:
                all_passed = False
        except urllib.error.HTTPError as e:
            all_passed = False
            print(f"[ERROR {e.code}] Kịch bản {i}: {e.read().decode('utf-8')}")
        time.sleep(0.3)
    return all_passed

time.sleep(3) # wait for backend startup

run1_ok = run_scenarios("LẦN CHẠY 1 (TẤT CẢ 12 KỊCH BẢN)")
run2_ok = run_scenarios("LẦN CHẠY 2 (KHÔNG RESET DỮ LIỆU - TỰ DỌN DẸP)")

print("\n==================== KẾT QUẢ TỔNG HỢP ====================")
print(f"Lần chạy 1: {'12/12 ĐẠT (PASS)' if run1_ok else 'THẤT BẠI'}")
print(f"Lần chạy 2: {'12/12 ĐẠT (PASS)' if run2_ok else 'THẤT BẠI'}")
if run1_ok and run2_ok:
    print("XÁC NHẬN: 2 lần chạy liên tiếp 100% ĐẠT, không làm hỏng dữ liệu của nhau!")
else:
    exit(1)
