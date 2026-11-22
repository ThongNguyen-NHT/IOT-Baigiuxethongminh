import urllib.request, urllib.error, json

def login(username, password):
    data = json.dumps({'username': username, 'password': password}).encode('utf-8')
    req = urllib.request.Request('http://localhost:5137/api/auth/login', data=data, headers={'Content-Type': 'application/json'})
    return json.loads(urllib.request.urlopen(req).read().decode('utf-8'))['accessToken']

cust_token = login('customer01', 'Customer@123')
staff_token = login('staff01', 'Staff@123')

# Test Customer calling /api/gate/in/scan -> Must be 403 Forbidden
scan_data = json.dumps({'entryCode': 'DUMMY123', 'bienSo': '51A-123.45'}).encode('utf-8')
req_cust = urllib.request.Request('http://localhost:5137/api/gate/in/scan', data=scan_data, headers={'Content-Type': 'application/json', 'Authorization': 'Bearer ' + cust_token})
try:
    urllib.request.urlopen(req_cust)
    print('ERROR: Customer should get 403!')
except urllib.error.HTTPError as e:
    print(f'Customer calling /api/gate/in/scan => {e.code} (Expected: 403 Forbidden)')

# Test Staff calling /api/gate/in/scan -> Must NOT be 403
req_staff = urllib.request.Request('http://localhost:5137/api/gate/in/scan', data=scan_data, headers={'Content-Type': 'application/json', 'Authorization': 'Bearer ' + staff_token})
try:
    urllib.request.urlopen(req_staff)
    print('Staff calling /api/gate/in/scan => 200 OK')
except urllib.error.HTTPError as e:
    print(f'Staff calling /api/gate/in/scan => {e.code} (Staff Authorized, rejected due to business: {e.reason})')

# Test running 12 scenarios
print('\n--- TESTING 12 SCENARIOS VIA /api/demo/scenarios/{id}/run ---')
for i in range(1, 13):
    req_scen = urllib.request.Request(f'http://localhost:5137/api/demo/scenarios/{i}/run', data=b'', headers={'Content-Type': 'application/json'})
    try:
        resp = urllib.request.urlopen(req_scen)
        res_json = json.loads(resp.read().decode('utf-8'))
        print(f"Scenario {i}: {res_json['name']} => Passed: {res_json['passed']}")
        print(f"   Expected: {res_json['expected']}")
        print(f"   Actual:   {res_json['actual']}")
    except urllib.error.HTTPError as e:
        err_body = e.read().decode('utf-8')
        print(f"Scenario {i} ERROR {e.code}: {err_body}")
