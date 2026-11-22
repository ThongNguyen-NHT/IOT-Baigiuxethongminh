/**
 * ====================================================================
 * ĐỒ ÁN CHUYÊN NGÀNH: HỆ THỐNG QUẢN LÝ BÃI ĐỖ XE THÔNG MINH (IOT SMART PARKING)
 * FIRMWARE ESP32 PHÂN TÁN ĐA BOARD (MULTI-BOARD DISTRIBUTED ARCHITECTURE)
 * ====================================================================
 * 
 * Mã nguồn hợp nhất (Unified Codebase) hỗ trợ 6 vai trò biên dịch qua PlatformIO:
 *   - [env:gate]  : Board Cổng kiểm soát (Barie In/Out, HC-SR04 Cổng vào, 3 Nút nhấn, LCD 20x4 I2C)
 *   - [env:row_e] : Board Hàng E (8 cảm biến siêu âm E1-E8, dải NeoPixel 8 LED)
 *   - [env:row_a] : Board Hàng A (8 cảm biến siêu âm A1-A8, dải NeoPixel 8 LED)
 *   - [env:row_b] : Board Hàng B (8 cảm biến siêu âm B1-B8, dải NeoPixel 8 LED)
 *   - [env:row_c] : Board Hàng C (8 cảm biến siêu âm C1-C8, dải NeoPixel 8 LED)
 *   - [env:row_d] : Board Hàng D (8 cảm biến siêu âm D1-D8, dải NeoPixel 8 LED)
 * 
 * Các board kết nối phân tán hoàn toàn qua WiFi/MQTT (không nối dây giữa các board).
 */

#include <Arduino.h>
#include <WiFi.h>
#include <PubSubClient.h>
#include <ArduinoJson.h>
#include <vector>

// =====================================================
// XÁC ĐỊNH VAI TRÒ BOARD VÀ CẤU HÌNH THIẾT BỊ
// =====================================================
#if !defined(NODE_ROLE_GATE) && !defined(NODE_ROLE_ROW)
  #define NODE_ROLE_ROW 1
  #define NODE_ID "esp32_row_e"
  #define ROW_PREFIX "E"
  #define PIXEL_OFFSET 10
  #define PIXEL_COUNT 8
#endif

#if defined(ROW_PREFIX)
  const char ROW_CHAR = ROW_PREFIX[0];
#else
  const char ROW_CHAR = 'E';
#endif

#if defined(NODE_ROLE_GATE)
  #include <ESP32Servo.h>
  #include <Wire.h>
  #include <LiquidCrystal_I2C.h>
#elif defined(NODE_ROLE_ROW)
  #include <Adafruit_NeoPixel.h>
#endif

// =====================================================
// THÔNG TIN MẠNG VÀ MQTT BROKER (HỖ TRỢ DỰ PHÒNG)
// =====================================================
const char *WIFI_SSID       = "Wokwi-GUEST";
const char *WIFI_PASSWORD   = "";

// Danh sách Broker công cộng (thay đổi khi một broker bị nghẽn/rate-limit):
// 1. Mặc định: "broker.hivemq.com" (Port 1883)
// 2. Dự phòng 1: "broker.emqx.io" (Port 1883)
// 3. Dự phòng 2: "test.mosquitto.org" (Port 1883)
const char *MQTT_BROKER     = "broker.hivemq.com";
const int   MQTT_PORT       = 1883;
const char *MQTT_PREFIX     = "dacn-smartparking/lot1";
const char *DEVICE_ID       = NODE_ID;

// =====================================================
// PHÂN BỔ CHÂN GPIO (THEO VAI TRÒ TỪNG BOARD)
// =====================================================
#if defined(NODE_ROLE_GATE)
  #define PIN_TRIG_GATE_IN    23
  #define PIN_ECHO_GATE_IN    34
  #define PIN_SERVO_GATE_IN   4
  #define PIN_SERVO_GATE_OUT  16
  #define PIN_BTN_GATE_OUT    18
  #define PIN_BTN_CP1         19
  #define PIN_BTN_CP2         32
  #define PIN_I2C_SDA         21
  #define PIN_I2C_SCL         22
#elif defined(NODE_ROLE_ROW)
  #define PIN_TRIG_ALL        23
  const uint8_t SLOT_ECHO_PINS[8] = { 35, 36, 39, 13, 14, 25, 26, 27 };
  #define PIN_NEOPIXEL        33
#endif

// =====================================================
// ĐỐI TƯỢNG PHẦN CỨNG & BIẾN TRẠNG THÁI TOÀN CỤC
// =====================================================
WiFiClient espClient;
PubSubClient mqttClient(espClient);

// Quản lý kết nối MQTT retry (Non-blocking)
unsigned long lastMqttRetryTime = 0;
unsigned long mqttRetryInterval = 2000;

// Hàng đợi ACK xác thực sự kiện quan trọng (RAM Queue)
struct PendingAck {
    String eventId;
    String topic;
    String payload;
    unsigned long sentAt;
    uint8_t retryCount;
    bool active;
};
#define MAX_PENDING_ACKS 8
PendingAck ackQueue[MAX_PENDING_ACKS];

#if defined(NODE_ROLE_GATE)
  LiquidCrystal_I2C lcd(0x27, 20, 4);
  Servo servoGateIn;
  Servo servoGateOut;

  #define BARRIER_CLOSED_ANGLE 0
  #define BARRIER_OPEN_ANGLE   90

  bool gateInOpen = false;
  unsigned long gateInOpenedAt = 0;
  unsigned long gateInTransitSec = 60;

  bool gateOutOpen = false;
  unsigned long gateOutOpenedAt = 0;

  #define NUM_SAMPLES 5
  float gateInSamples[NUM_SAMPLES];
  uint8_t gateInSampleIdx = 0;
  bool gateInFilled = false;
  float gateInCurrentDist = 200.0;
  enum GateInPassState { PASS_IDLE, CAR_APPROACHING, CAR_AT_GATE, CAR_EXITED };
  GateInPassState gateInPassState = PASS_IDLE;
  unsigned long gateInCarDetectedAt = 0;

  unsigned long lastGateSensorScanTime = 0;
  unsigned long lastLcdUpdateTime = 0;
  bool bootScreenDone = false;
  unsigned long bootStartedAt = 0;
  String lcdLine1 = "== SMART PARKING ==";
  String lcdLine2 = "CONG: SAN SANG";
#elif defined(NODE_ROLE_ROW)
  Adafruit_NeoPixel strip(PIXEL_COUNT, PIN_NEOPIXEL, NEO_GRB + NEO_KHZ800);

  #define NUM_SAMPLES 5
  struct SlotSensor {
      String name;
      uint8_t echoPin;
      float samples[NUM_SAMPLES];
      uint8_t sampleIdx;
      bool bufferFilled;
      float currentDist;
      float lastPublishedDist;
      unsigned long lastPublishedAt; // Giới hạn tần suất: tối thiểu 300ms giữa 2 lần gửi của cùng 1 ô
      String currentState;          // "FREE", "OCCUPIED", "OVERLAP"
      String candidateState;
      unsigned long candidateSince;
  };

  SlotSensor slots[8];
  unsigned long lastSensorScanTime = 0;
  const unsigned long SENSOR_SCAN_INTERVAL = 60;

  bool guideActive = false;
  unsigned long guideExpireAt = 0;

  unsigned long lastSnapshotPublishedAt = 0;
  const unsigned long MIN_SNAPSHOT_INTERVAL = 5000; // Tối thiểu 5 giây giữa 2 snapshot
  unsigned long lastPeriodicSnapshotTime = 0;
  const unsigned long PERIODIC_SNAPSHOT_INTERVAL = 30000; // 30s gửi snapshot định kỳ
#endif

// =====================================================
// KHAI BÁO NGUYÊN MẪU HÀM (FORWARD DECLARATIONS)
// =====================================================
void setupPins();
void initHardware();
void connectWiFi();
void checkMqttConnection();
void mqttCallback(char *topic, byte *payload, unsigned int length);
void handleAck(const JsonDocument &doc);
void updateAckQueue();
void sendReliableEvent(const String &topic, const String &type, const String &eventId);
void publishDevicePower();
float computeMedian(float arr[], int n);
const char* getMqttStateStr(int state);

#if defined(NODE_ROLE_GATE)
  void scanGateInSensor();
  void updateGateInPassDetection();
  void updateGateServos();
  void checkButtons();
  void updateLcdDisplay();
  void openGateIn(unsigned long transitSec);
  void closeGateIn();
  void openGateOut();
  void closeGateOut();
  void handleGateInCmd(const JsonDocument &doc);
  void handleGateOutCmd(const JsonDocument &doc);
  void handleDisplayCmd(const JsonDocument &doc);
#elif defined(NODE_ROLE_ROW)
  void scanAllSensors();
  float readUltrasonicCm(uint8_t echoPin);
  void processSlotDebounce(SlotSensor &s);
  void publishSlotState(const String &slotId, float distCm, bool retain = true);
  void publishAllSlotsSnapshot(bool force = false);
  void handleSlotCmd(const String &slotId, const String &colorCmd);
  void handleGuideCmd(const JsonDocument &doc);
  void updateGuideLed();
#endif

#define FIRMWARE_VERSION "v3.0-multi-board"

// =====================================================
// SETUP CHUNG
// =====================================================
void setup() {
    Serial.begin(115200);
    delay(500); // Cho phép Wokwi UART terminal kết nối đầy đủ

    Serial.println();
    Serial.println("========================================================");
    Serial.printf("[BOOT] NODE_ID: %s | ROLE: %s | VERSION: %s\n",
                  DEVICE_ID,
#if defined(NODE_ROLE_GATE)
                  "GATE CONTROLLER",
#elif defined(NODE_ROLE_ROW)
                  "ROW CONTROLLER",
#endif
                  FIRMWARE_VERSION);
    Serial.println("========================================================");

    setupPins();
    initHardware();
#if defined(NODE_ROLE_GATE)
    bootStartedAt = millis();
#endif
    connectWiFi();

    mqttClient.setServer(MQTT_BROKER, MQTT_PORT);
    mqttClient.setBufferSize(1024);
    mqttClient.setKeepAlive(30);
    mqttClient.setCallback(mqttCallback);

    Serial.printf("[SETUP] Khởi động hoàn tất cho board %s.\n", DEVICE_ID);
}

// =====================================================
// MAIN LOOP (KHÔNG BLOCKING)
// =====================================================
void loop() {
    unsigned long now = millis();

    // 1. Duy trì kết nối mạng WiFi & MQTT
    if (WiFi.status() == WL_CONNECTED) {
        static bool wasMqttConnected = false;
        bool isMqttConnected = mqttClient.connected();

        if (!isMqttConnected) {
            if (wasMqttConnected) {
                int err = mqttClient.state();
                Serial.printf("[MQTT][%s] MẤT KẾT NỐI! Mã trạng thái state() = %d (%s). Bắt đầu backoff kết nối lại...\n",
                              DEVICE_ID, err, getMqttStateStr(err));
            }
            if (now - lastMqttRetryTime >= mqttRetryInterval) {
                lastMqttRetryTime = now;
                checkMqttConnection();
            }
        } else {
            mqttClient.loop();
            updateAckQueue();
        }
        wasMqttConnected = isMqttConnected;
    }

#if defined(NODE_ROLE_GATE)
    // 2. Quét cảm biến siêu âm cổng vào (chu kỳ 80ms)
    if (now - lastGateSensorScanTime >= 80) {
        lastGateSensorScanTime = now;
        scanGateInSensor();
    }

    // 3. Giám sát logic xe qua cổng vào HC-SR04
    updateGateInPassDetection();

    // 4. Giám sát các nút nhấn (Cổng ra, CP1, CP2)
    checkButtons();

    // 5. Cập nhật góc quay servo barie (auto-close & transit timeout)
    updateGateServos();

    // 6. Cập nhật màn hình LCD định kỳ 1s
    if (now - lastLcdUpdateTime >= 1000) {
        lastLcdUpdateTime = now;
        updateLcdDisplay();
    }

#elif defined(NODE_ROLE_ROW)
    // 2. Quét tuần tự 8 cảm biến ô đỗ của hàng này
    if (now - lastSensorScanTime >= SENSOR_SCAN_INTERVAL) {
        lastSensorScanTime = now;
        scanAllSensors();
    }

    // 3. Tắt LED dẫn đường khi hết hạn TTL
    updateGuideLed();

    // 4. Snapshot định kỳ 30s
    if (mqttClient.connected() && (now - lastPeriodicSnapshotTime >= PERIODIC_SNAPSHOT_INTERVAL)) {
        publishAllSlotsSnapshot(false);
    }
#endif
}

// =====================================================
// THIẾT LẬP PHẦN CỨNG & NGOẠI VI
// =====================================================
void setupPins() {
#if defined(NODE_ROLE_GATE)
    pinMode(PIN_TRIG_GATE_IN, OUTPUT);
    digitalWrite(PIN_TRIG_GATE_IN, LOW);
    pinMode(PIN_ECHO_GATE_IN, INPUT);

    pinMode(PIN_BTN_GATE_OUT, INPUT_PULLUP);
    pinMode(PIN_BTN_CP1, INPUT_PULLUP);
    pinMode(PIN_BTN_CP2, INPUT_PULLUP);

    for (int i = 0; i < NUM_SAMPLES; i++) gateInSamples[i] = 200.0;
#elif defined(NODE_ROLE_ROW)
    pinMode(PIN_TRIG_ALL, OUTPUT);
    digitalWrite(PIN_TRIG_ALL, LOW);

    for (int i = 0; i < 8; i++) {
        slots[i].name = String(ROW_CHAR) + String(i + 1);
        slots[i].echoPin = SLOT_ECHO_PINS[i];
        slots[i].sampleIdx = 0;
        slots[i].bufferFilled = false;
        slots[i].currentDist = 200.0;
        slots[i].lastPublishedDist = -1;
        slots[i].lastPublishedAt = 0;
        slots[i].currentState = "FREE";
        slots[i].candidateState = "FREE";
        slots[i].candidateSince = 0;
        for (int s = 0; s < NUM_SAMPLES; s++) slots[i].samples[s] = 200.0;
        pinMode(slots[i].echoPin, INPUT);
    }
#endif

    for (int i = 0; i < MAX_PENDING_ACKS; i++) {
        ackQueue[i].active = false;
    }
}

void initHardware() {
#if defined(NODE_ROLE_GATE)
    // 1. Servos Barie
    ESP32PWM::allocateTimer(0);
    ESP32PWM::allocateTimer(1);
    servoGateIn.setPeriodHertz(50);
    servoGateOut.setPeriodHertz(50);
    servoGateIn.attach(PIN_SERVO_GATE_IN, 500, 2400);
    servoGateOut.attach(PIN_SERVO_GATE_OUT, 500, 2400);
    servoGateIn.write(BARRIER_CLOSED_ANGLE);
    servoGateOut.write(BARRIER_CLOSED_ANGLE);

    // 2. Màn hình LCD 20x4 I2C (Kiểm tra I2C Bus & địa chỉ 0x27 / 0x3F)
    Wire.begin(PIN_I2C_SDA, PIN_I2C_SCL);
    delay(50);

    Wire.beginTransmission(0x27);
    byte i2cErr = Wire.endTransmission();
    if (i2cErr == 0) {
        Serial.printf("[LCD] Tìm thấy LCD 20x4 I2C tại địa chỉ 0x27 (SDA=%d, SCL=%d).\n", PIN_I2C_SDA, PIN_I2C_SCL);
    } else {
        Wire.beginTransmission(0x3F);
        if (Wire.endTransmission() == 0) {
            Serial.printf("[LCD] CẢNH BÁO: LCD phản hồi tại 0x3F thay vì 0x27! (SDA=%d, SCL=%d)\n", PIN_I2C_SDA, PIN_I2C_SCL);
        } else {
            Serial.printf("[LCD] LỖI: Không tìm thấy thiết bị I2C tại 0x27 hay 0x3F (err=%d). Kiểm tra dây SDA=%d, SCL=%d!\n", i2cErr, PIN_I2C_SDA, PIN_I2C_SCL);
        }
    }

    lcd.init();
    lcd.backlight();
    lcd.clear();
    lcd.setCursor(0, 0); lcd.print("== SMART PARKING ==");
    lcd.setCursor(0, 1); lcd.print("esp32_gate-khoi dong");
    lcd.setCursor(0, 2); lcd.print("WIFI: CHO KET NOI   ");
    lcd.setCursor(0, 3); lcd.print("MQTT: CHO KET NOI   ");
#elif defined(NODE_ROLE_ROW)
    // NeoPixel Strip cho hàng này
    strip.begin();
    strip.setBrightness(180);
    for (int i = 0; i < PIXEL_COUNT; i++) strip.setPixelColor(i, strip.Color(0, 255, 0)); // Mặc định xanh (FREE)
    strip.show();
#endif
}

// =====================================================
// KẾT NỐI WIFI VÀ MQTT
// =====================================================
void connectWiFi() {
    Serial.printf("[WIFI] Đang nối vào mạng '%s'...\n", WIFI_SSID);
#if defined(NODE_ROLE_GATE)
    lcd.setCursor(0, 2); lcd.print("WIFI: DANG NOI...   ");
#endif
    WiFi.mode(WIFI_STA);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    int attempts = 0;
    while (WiFi.status() != WL_CONNECTED && attempts < 25) {
        delay(300);
        attempts++;
        Serial.printf("[WIFI] Đang nối... (lần %d/25)\n", attempts);
#if defined(NODE_ROLE_GATE)
        lcd.setCursor(0, 2);
        char wbuf[21];
        snprintf(wbuf, sizeof(wbuf), "WIFI: DANG NOI (%d) ", attempts);
        lcd.print(wbuf);
#endif
    }

    if (WiFi.status() == WL_CONNECTED) {
        Serial.printf("[WIFI] ĐÃ NỐI THÀNH CÔNG! IP: %s | RSSI: %d dBm\n",
                      WiFi.localIP().toString().c_str(), WiFi.RSSI());
#if defined(NODE_ROLE_GATE)
        lcd.setCursor(0, 2);
        lcd.print("WIFI: DA NOI OK     ");
#endif
    } else {
        Serial.printf("[WIFI] CHƯA NỐI ĐƯỢC (Status: %d). Sẽ thử lại trong loop.\n", WiFi.status());
#if defined(NODE_ROLE_GATE)
        lcd.setCursor(0, 2);
        lcd.print("WIFI: MAT KET NOI   ");
#endif
    }
}

const char* getMqttStateStr(int state) {
    switch (state) {
        case -4: return "TIMEOUT (-4)";
        case -3: return "CONNECTION_LOST (-3)";
        case -2: return "CONNECT_FAILED (-2)";
        case -1: return "DISCONNECTED (-1)";
        case 0:  return "CONNECTED (0)";
        case 1:  return "BAD_PROTOCOL (1)";
        case 2:  return "BAD_CLIENT_ID (2)";
        case 3:  return "UNAVAILABLE (3)";
        case 4:  return "BAD_CREDENTIALS (4)";
        case 5:  return "UNAUTHORIZED (5)";
        default: return "UNKNOWN";
    }
}

void checkMqttConnection() {
    if (WiFi.status() != WL_CONNECTED) {
        Serial.println("[MQTT] Chưa có WiFi, bỏ qua kết nối MQTT.");
        return;
    }

    String willTopic = String(MQTT_PREFIX) + "/device/" + DEVICE_ID + "/status";
    String clientId = "ESP32_" + String(DEVICE_ID) + "_" + String((uint32_t)ESP.getEfuseMac(), HEX);

    Serial.printf("[MQTT] %s đang nối tới %s:%d (LWT: %s = OFFLINE)...\n",
                  DEVICE_ID, MQTT_BROKER, MQTT_PORT, willTopic.c_str());
#if defined(NODE_ROLE_GATE)
    lcd.setCursor(0, 3);
    lcd.print("MQTT: DANG NOI...   ");
#endif

    if (mqttClient.connect(clientId.c_str(), willTopic.c_str(), 1, true, "OFFLINE")) {
        Serial.printf("[MQTT] %s ĐÃ NỐI THÀNH CÔNG!\n", DEVICE_ID);
#if defined(NODE_ROLE_GATE)
        lcd.setCursor(0, 3);
        lcd.print("MQTT: DA NOI OK     ");
#endif
        mqttRetryInterval = 2000;

        // 1. Công bố trạng thái ONLINE (Retained)
        mqttClient.publish(willTopic.c_str(), "ONLINE", true);

        // 2. Báo cáo thông tin nguồn điện
        publishDevicePower();

        // 3. Đăng ký topic theo vai trò
        String subPrefix = String(MQTT_PREFIX);
        mqttClient.subscribe((subPrefix + "/ack").c_str(), 1);
        mqttClient.subscribe((subPrefix + "/device/" + DEVICE_ID + "/cmd").c_str(), 1);

#if defined(NODE_ROLE_GATE)
        mqttClient.subscribe((subPrefix + "/gate/in/cmd").c_str(), 1);
        mqttClient.subscribe((subPrefix + "/gate/out/cmd").c_str(), 1);
        mqttClient.subscribe((subPrefix + "/display/cmd").c_str(), 1);
        Serial.printf("[MQTT] Board GATE đã subscribe: gate/in/cmd, gate/out/cmd, display/cmd, ack, device/%s/cmd\n", DEVICE_ID);
#elif defined(NODE_ROLE_ROW)
        mqttClient.subscribe((subPrefix + "/slot/+/cmd").c_str(), 1);
        mqttClient.subscribe((subPrefix + "/guide/cmd").c_str(), 1);
        mqttClient.subscribe((subPrefix + "/resync").c_str(), 1);
        Serial.printf("[MQTT] Board %s đã subscribe: slot/+/cmd, guide/cmd, resync, ack, device/%s/cmd\n", DEVICE_ID, DEVICE_ID);

        // 4. Phát snapshot khởi đầu cho các ô của hàng này
        publishAllSlotsSnapshot(true);
#endif
    } else {
        int errCode = mqttClient.state();
        Serial.printf("[MQTT] %s LỖI KẾT NỐI (Mã lỗi rc = %d: %s). Thử lại sau %lu ms...\n",
                      DEVICE_ID, errCode, getMqttStateStr(errCode), mqttRetryInterval);
#if defined(NODE_ROLE_GATE)
        lcd.setCursor(0, 3);
        char mbuf[21];
        snprintf(mbuf, sizeof(mbuf), "MQTT: LOI rc=%d     ", errCode);
        lcd.print(mbuf);
#endif
        mqttRetryInterval = min(mqttRetryInterval * 2, 15000UL);
    }
}

// =====================================================
// XỬ LÝ LỆNH TỪ BACKEND (MQTT CALLBACK)
// =====================================================
void mqttCallback(char *topic, byte *payload, unsigned int length) {
    char payloadStr[length + 1];
    memcpy(payloadStr, payload, length);
    payloadStr[length] = '\0';
    String topicStr = String(topic);
    String body = String(payloadStr);

    Serial.printf("[MQTT <- IN][%s] Topic: %s | Payload: %s\n", DEVICE_ID, topic, payloadStr);

    StaticJsonDocument<512> doc;
    DeserializationError err = deserializeJson(doc, payloadStr);

    // Xác nhận ACK chung cho mọi board
    if (topicStr.endsWith("/ack")) {
        if (!err) handleAck(doc);
        return;
    }

#if defined(NODE_ROLE_GATE)
    if (topicStr.endsWith("/gate/in/cmd")) {
        if (!err) handleGateInCmd(doc);
        return;
    }
    if (topicStr.endsWith("/gate/out/cmd")) {
        if (!err) handleGateOutCmd(doc);
        return;
    }
    if (topicStr.endsWith("/display/cmd")) {
        if (!err) handleDisplayCmd(doc);
        return;
    }
#elif defined(NODE_ROLE_ROW)
    // 1. Lệnh điều khiển đèn LED ô: slot/{id}/cmd
    int slotCmdIdx = topicStr.indexOf("/slot/");
    if (slotCmdIdx != -1 && topicStr.endsWith("/cmd")) {
        String slotId = topicStr.substring(slotCmdIdx + 6, topicStr.length() - 4);
        handleSlotCmd(slotId, body);
        return;
    }

    // 2. Lệnh dẫn đường NeoPixel: guide/cmd
    if (topicStr.endsWith("/guide/cmd")) {
        if (!err) handleGuideCmd(doc);
        return;
    }

    // 3. Yêu cầu snapshot/resync: topic resync HOẶC device/{NODE_ID}/cmd chứa resync
    bool isResyncTopic = topicStr.endsWith("/resync");
    bool isDeviceResyncCmd = (topicStr.indexOf("/device/") != -1 && topicStr.endsWith("/cmd") &&
                             (body.indexOf("resync") != -1 || body.indexOf("SNAPSHOT") != -1 || (!err && doc.containsKey("resync"))));
    if (isResyncTopic || isDeviceResyncCmd) {
        Serial.printf("[CMD][%s] Nhận lệnh resync/snapshot -> Đang gửi snapshot hàng %c...\n", DEVICE_ID, ROW_CHAR);
        publishAllSlotsSnapshot(false);
        return;
    }
#endif
}

// =====================================================
// XỬ LÝ ACK VÀ ĐỘ TIN CẬY (RAM QUEUE RETRY)
// =====================================================
void handleAck(const JsonDocument &doc) {
    String ackId = doc["eventId"] | "";
    if (ackId.isEmpty()) return;

    for (int i = 0; i < MAX_PENDING_ACKS; i++) {
        if (ackQueue[i].active && ackQueue[i].eventId == ackId) {
            ackQueue[i].active = false;
            Serial.printf("[ACK] Board %s đã nhận xác nhận thành công cho eventId: %s\n",
                          DEVICE_ID, ackId.c_str());
            break;
        }
    }
}

void sendReliableEvent(const String &topic, const String &type, const String &eventId) {
    StaticJsonDocument<256> doc;
    doc["type"] = type;
    doc["eventId"] = eventId;
    char buffer[256];
    serializeJson(doc, buffer);

    mqttClient.publish(topic.c_str(), buffer, false);
    Serial.printf("[EVENT -> MQTT][%s] %s | %s\n", DEVICE_ID, topic.c_str(), buffer);

    bool queued = false;
    for (int i = 0; i < MAX_PENDING_ACKS; i++) {
        if (!ackQueue[i].active) {
            ackQueue[i].eventId = eventId;
            ackQueue[i].topic = topic;
            ackQueue[i].payload = String(buffer);
            ackQueue[i].sentAt = millis();
            ackQueue[i].retryCount = 0;
            ackQueue[i].active = true;
            queued = true;
            break;
        }
    }
    // Hàng đợi đầy: ghi đè phần tử cũ nhất một cách an toàn, không làm treo vòng lặp
    if (!queued) {
        int oldestIdx = 0;
        unsigned long oldestTime = ackQueue[0].sentAt;
        for (int i = 1; i < MAX_PENDING_ACKS; i++) {
            if (ackQueue[i].sentAt < oldestTime) {
                oldestTime = ackQueue[i].sentAt;
                oldestIdx = i;
            }
        }
        Serial.printf("[ACK_QUEUE][%s] Hàng đợi đầy, thay thế phần tử cũ nhất: %s\n",
                      DEVICE_ID, ackQueue[oldestIdx].eventId.c_str());
        ackQueue[oldestIdx].eventId = eventId;
        ackQueue[oldestIdx].topic = topic;
        ackQueue[oldestIdx].payload = String(buffer);
        ackQueue[oldestIdx].sentAt = millis();
        ackQueue[oldestIdx].retryCount = 0;
        ackQueue[oldestIdx].active = true;
    }
}

void updateAckQueue() {
    unsigned long now = millis();
    for (int i = 0; i < MAX_PENDING_ACKS; i++) {
        if (ackQueue[i].active) {
            if (now - ackQueue[i].sentAt >= 2000) {
                if (ackQueue[i].retryCount < 3) {
                    ackQueue[i].retryCount++;
                    ackQueue[i].sentAt = now;
                    mqttClient.publish(ackQueue[i].topic.c_str(), ackQueue[i].payload.c_str(), false);
                    Serial.printf("[RETRY][%s] Thử gửi lại lần %d cho eventId: %s\n",
                                  DEVICE_ID, ackQueue[i].retryCount, ackQueue[i].eventId.c_str());
                } else {
                    Serial.printf("[TIMEOUT][%s] Hết số lần thử (3 lần) cho eventId: %s. Hủy gói tin.\n",
                                  DEVICE_ID, ackQueue[i].eventId.c_str());
                    ackQueue[i].active = false;
                }
            }
        }
    }
}

void publishDevicePower() {
    String powerTopic = String(MQTT_PREFIX) + "/device/" + DEVICE_ID + "/power";
    StaticJsonDocument<128> doc;
    doc["batteryVolt"] = 3.7;
    doc["powerSource"] = "MAIN";
    doc["deviceId"] = DEVICE_ID;
    char buffer[128];
    serializeJson(doc, buffer);
    mqttClient.publish(powerTopic.c_str(), buffer, true);
}

// Hàm lọc nhiễu trung vị
float computeMedian(float arr[], int n) {
    if (n <= 0) return 200.0;
    float temp[NUM_SAMPLES];
    for (int i = 0; i < n; i++) temp[i] = arr[i];

    for (int i = 0; i < n - 1; i++) {
        for (int j = i + 1; j < n; j++) {
            if (temp[i] > temp[j]) {
                float t = temp[i];
                temp[i] = temp[j];
                temp[j] = t;
            }
        }
    }
    return temp[n / 2];
}

// =====================================================
// KHỐI CODE RIÊNG CHO BOARD GATE CONTROLLER
// =====================================================
#if defined(NODE_ROLE_GATE)
void scanGateInSensor() {
    digitalWrite(PIN_TRIG_GATE_IN, LOW);
    delayMicroseconds(2);
    digitalWrite(PIN_TRIG_GATE_IN, HIGH);
    delayMicroseconds(10);
    digitalWrite(PIN_TRIG_GATE_IN, LOW);

    unsigned long duration = pulseIn(PIN_ECHO_GATE_IN, HIGH, 25000);
    float cm = 200.0;
    if (duration > 0) {
        cm = (duration * 0.0343) / 2.0;
        if (cm < 2.0) cm = 2.0;
        if (cm > 400.0) cm = 400.0;
    }

    gateInSamples[gateInSampleIdx] = cm;
    gateInSampleIdx = (gateInSampleIdx + 1) % NUM_SAMPLES;
    if (gateInSampleIdx == 0) gateInFilled = true;
    gateInCurrentDist = computeMedian(gateInSamples, gateInFilled ? NUM_SAMPLES : gateInSampleIdx);
}

void updateGateInPassDetection() {
    // QUY TẮC BẢO VỆ CỔNG VÀO:
    // Cảm biến cổng vào CHỈ phát sự kiện 'pass' khi cổng vào ĐANG MỞ (do Backend phát lệnh OPEN)
    if (!gateInOpen) {
        gateInPassState = PASS_IDLE;
        static unsigned long lastUnauthorizedWarn = 0;
        if (gateInCurrentDist <= 40.0 && (millis() - lastUnauthorizedWarn >= 4000)) {
            lastUnauthorizedWarn = millis();
            Serial.println("[GATE_IN_ALERT] Cổng vào đang ĐÓNG nhưng cảm biến phát hiện xe! Bỏ qua, không gửi sự kiện 'pass'.");
            String alertPayload = "{\"type\":\"UNAUTHORIZED_ENTRY\",\"message\":\"Phat hien xe tiep can khi cong vao dang dong\",\"ts\":\"" + String(millis()) + "\"}";
            mqttClient.publish((String(MQTT_PREFIX) + "/alert").c_str(), alertPayload.c_str());
        }
        return;
    }

    // Logic máy trạng thái (FSM) xác nhận xe đi qua
    if (gateInPassState == PASS_IDLE) {
        if (gateInCurrentDist <= 40.0) {
            gateInPassState = CAR_APPROACHING;
            gateInCarDetectedAt = millis();
            Serial.println("[GATE_IN] Phát hiện đầu xe tiếp cận cổng vào.");
        }
    } else if (gateInPassState == CAR_APPROACHING) {
        if (millis() - gateInCarDetectedAt >= 1500) {
            gateInPassState = CAR_AT_GATE;
            Serial.println("[GATE_IN] Xe đang nằm trong vùng quét cổng vào.");
        }
    } else if (gateInPassState == CAR_AT_GATE) {
        if (gateInCurrentDist > 70.0) {
            gateInPassState = PASS_IDLE;
            Serial.println("[GATE_IN] XÁC NHẬN: Xe đã đi qua cổng vào (PASS)!");

            closeGateIn();

            String evtId = "in_evt_" + String(millis()) + "_" + String(random(100, 999));
            sendReliableEvent(String(MQTT_PREFIX) + "/gate/in/event", "pass", evtId);
        }
    }
}

void updateGateServos() {
    unsigned long now = millis();
    if (gateInOpen && (now - gateInOpenedAt >= gateInTransitSec * 1000)) {
        Serial.printf("[GATE_IN] Hết hạn transitSec (%lu s) -> Tự động đóng barie cổng vào.\n", gateInTransitSec);
        closeGateIn();
    }
    if (gateOutOpen && (now - gateOutOpenedAt >= 3000)) {
        closeGateOut();
    }
}

void checkButtons() {
    static int lastBtnGateOut = HIGH;
    static int lastBtnCp1 = HIGH;
    static int lastBtnCp2 = HIGH;
    static unsigned long lastDebounceTime = 0;

    int rGateOut = digitalRead(PIN_BTN_GATE_OUT);
    int rCp1     = digitalRead(PIN_BTN_CP1);
    int rCp2     = digitalRead(PIN_BTN_CP2);

    if (millis() - lastDebounceTime > 250) {
        if (rGateOut == LOW && lastBtnGateOut == HIGH) {
            lastDebounceTime = millis();
            Serial.println("[BUTTON] Bấm nút Cổng ra -> Kích hoạt xe rời bãi.");
            openGateOut();
            String evtId = "out_evt_" + String(millis()) + "_" + String(random(100, 999));
            sendReliableEvent(String(MQTT_PREFIX) + "/gate/out/event", "leave", evtId);
        }
        if (rCp1 == LOW && lastBtnCp1 == HIGH) {
            lastDebounceTime = millis();
            Serial.println("[BUTTON] Bấm nút Checkpoint 1 (CP1) -> Kích hoạt gia hạn.");
            String evtId = "cp1_evt_" + String(millis()) + "_" + String(random(100, 999));
            sendReliableEvent(String(MQTT_PREFIX) + "/checkpoint/CP1/event", "pass", evtId);
        }
        if (rCp2 == LOW && lastBtnCp2 == HIGH) {
            lastDebounceTime = millis();
            Serial.println("[BUTTON] Bấm nút Checkpoint 2 (CP2) -> Kích hoạt gia hạn.");
            String evtId = "cp2_evt_" + String(millis()) + "_" + String(random(100, 999));
            sendReliableEvent(String(MQTT_PREFIX) + "/checkpoint/CP2/event", "pass", evtId);
        }
    }
    lastBtnGateOut = rGateOut;
    lastBtnCp1 = rCp1;
    lastBtnCp2 = rCp2;
}

void updateLcdDisplay() {
    // Trong 6 giây đầu sau khi bật nguồn, giữ màn hình "esp32_gate - khoi dong" cùng trạng thái WiFi/MQTT
    if (millis() - bootStartedAt < 6000 && !bootScreenDone) {
        lcd.setCursor(0, 0); lcd.print("== SMART PARKING ==");
        lcd.setCursor(0, 1); lcd.print("esp32_gate-khoi dong");
        lcd.setCursor(0, 2);
        if (WiFi.status() == WL_CONNECTED) lcd.print("WIFI: DA NOI OK     ");
        else lcd.print("WIFI: DANG NOI...   ");
        lcd.setCursor(0, 3);
        if (mqttClient.connected()) lcd.print("MQTT: DA NOI OK     ");
        else lcd.print("MQTT: CHO KET NOI   ");
        return;
    }
    bootScreenDone = true;

    lcd.setCursor(0, 0);
    lcd.print("== SMART PARKING ==");

    lcd.setCursor(0, 1);
    if (gateInOpen) {
        lcd.print("CUA VAO: DANG MO   ");
    } else {
        lcd.print("CUA VAO: DA DONG   ");
    }

    lcd.setCursor(0, 2);
    char buf[21];
    snprintf(buf, sizeof(buf), "C/BIEN VAO: %3.0f cm", gateInCurrentDist);
    lcd.print(buf);

    lcd.setCursor(0, 3);
    if (mqttClient.connected()) {
        lcd.print("MQTT: DA NOI OK    ");
    } else {
        lcd.print("MQTT: MAT KET NOI  ");
    }
}

void openGateIn(unsigned long transitSec) {
    servoGateIn.write(BARRIER_OPEN_ANGLE);
    gateInOpen = true;
    gateInOpenedAt = millis();
    gateInTransitSec = transitSec > 0 ? transitSec : 60;
    gateInPassState = PASS_IDLE;
    Serial.printf("[BARRIER_IN] MỞ CỔNG VÀO (Hạn qua cổng: %lu s)\n", gateInTransitSec);
}

void closeGateIn() {
    servoGateIn.write(BARRIER_CLOSED_ANGLE);
    gateInOpen = false;
    gateInPassState = PASS_IDLE;
    Serial.println("[BARRIER_IN] ĐÃ ĐÓNG CỔNG VÀO.");
}

void openGateOut() {
    servoGateOut.write(BARRIER_OPEN_ANGLE);
    gateOutOpen = true;
    gateOutOpenedAt = millis();
    Serial.println("[BARRIER_OUT] MỞ CỔNG RA.");
}

void closeGateOut() {
    servoGateOut.write(BARRIER_CLOSED_ANGLE);
    gateOutOpen = false;
    Serial.println("[BARRIER_OUT] ĐÃ ĐÓNG CỔNG RA.");
}

void handleGateInCmd(const JsonDocument &doc) {
    String cmd = doc["cmd"] | "";
    if (cmd.equalsIgnoreCase("OPEN")) {
        unsigned long transitSec = doc["transitSec"] | 60;
        openGateIn(transitSec);
    } else if (cmd.equalsIgnoreCase("HOLD") || cmd.equalsIgnoreCase("CLOSE")) {
        closeGateIn();
    }
}

void handleGateOutCmd(const JsonDocument &doc) {
    String cmd = doc["cmd"] | "";
    if (cmd.equalsIgnoreCase("OPEN")) {
        openGateOut();
    } else if (cmd.equalsIgnoreCase("CLOSE")) {
        closeGateOut();
    }
}

void handleDisplayCmd(const JsonDocument &doc) {
    String line1 = doc["line1"] | "";
    String line2 = doc["line2"] | "";
    if (line1.length() > 0) lcdLine1 = line1;
    if (line2.length() > 0) lcdLine2 = line2;
    lcd.setCursor(0, 1);
    char buf[21];
    snprintf(buf, sizeof(buf), "%-20s", lcdLine1.c_str());
    lcd.print(buf);
}
#endif

// =====================================================
// KHỐI CODE RIÊNG CHO CÁC BOARD HÀNG Ô (ROW CONTROLLER)
// =====================================================
#if defined(NODE_ROLE_ROW)
float readUltrasonicCm(uint8_t echoPin) {
    digitalWrite(PIN_TRIG_ALL, LOW);
    delayMicroseconds(2);
    digitalWrite(PIN_TRIG_ALL, HIGH);
    delayMicroseconds(10);
    digitalWrite(PIN_TRIG_ALL, LOW);

    unsigned long duration = pulseIn(echoPin, HIGH, 25000);
    if (duration == 0) return 200.0; // Ngoài tầm đo -> Ô trống

    float cm = (duration * 0.0343) / 2.0;
    if (cm < 2.0) cm = 2.0;
    if (cm > 400.0) cm = 400.0;
    return cm;
}

void scanAllSensors() {
    for (int i = 0; i < 8; i++) {
        float dist = readUltrasonicCm(slots[i].echoPin);
        if (dist > 0) {
            slots[i].samples[slots[i].sampleIdx] = dist;
            slots[i].sampleIdx = (slots[i].sampleIdx + 1) % NUM_SAMPLES;
            if (slots[i].sampleIdx == 0) slots[i].bufferFilled = true;

            slots[i].currentDist = computeMedian(slots[i].samples, slots[i].bufferFilled ? NUM_SAMPLES : slots[i].sampleIdx);
            processSlotDebounce(slots[i]);
        }
        delayMicroseconds(100);
    }
}

void processSlotDebounce(SlotSensor &s) {
    String rawState = "FREE";
    if (s.currentDist <= 40.0) {
        rawState = "OCCUPIED";
    } else if (s.currentDist <= 80.0) {
        rawState = "OVERLAP";
    }

    if (rawState != s.candidateState) {
        s.candidateState = rawState;
        s.candidateSince = millis();
    } else {
        if (millis() - s.candidateSince >= 1500) { // 1.5s Debounce
            unsigned long now = millis();
            bool stateChanged = (s.currentState != s.candidateState);
            // Bỏ qua thay đổi nhỏ: chỉ gửi nếu đổi trạng thái HOẶC khoảng cách thay đổi >= 10.0 cm
            bool distChangedSignificantly = (abs(s.currentDist - s.lastPublishedDist) >= 10.0);

            if (stateChanged || distChangedSignificantly) {
                // Giới hạn tần suất: tối thiểu 300ms giữa 2 lần gửi cho cùng một ô (gộp các thay đổi nhanh)
                if (now - s.lastPublishedAt >= 300) {
                    s.currentState = s.candidateState;
                    s.lastPublishedAt = now;
                    publishSlotState(s.name, s.currentDist, true);
                }
            }
        }
    }
}

void publishSlotState(const String &slotId, float distCm, bool retain) {
    if (!mqttClient.connected()) return;

    for (int i = 0; i < 8; i++) {
        if (slots[i].name.equalsIgnoreCase(slotId)) {
            slots[i].lastPublishedDist = distCm;
            break;
        }
    }

    String topic = String(MQTT_PREFIX) + "/slot/" + slotId + "/state";
    StaticJsonDocument<128> doc;
    doc["spot"] = slotId;
    doc["distanceCm"] = round(distCm * 10.0) / 10.0;
    char buffer[128];
    serializeJson(doc, buffer);

    mqttClient.publish(topic.c_str(), buffer, retain);
}

void publishAllSlotsSnapshot(bool force) {
    if (!mqttClient.connected()) return;

    unsigned long now = millis();
    if (!force && (now - lastSnapshotPublishedAt < MIN_SNAPSHOT_INTERVAL)) {
        Serial.printf("[SNAPSHOT][%s] Bỏ qua vì chưa đủ 5 giây kể từ lần snapshot trước.\n", DEVICE_ID);
        return;
    }
    lastSnapshotPublishedAt = now;
    lastPeriodicSnapshotTime = now;

    Serial.printf("[SNAPSHOT][%s] Đang công bố toàn bộ trạng thái 8 ô hàng %c...\n", DEVICE_ID, ROW_CHAR);
    for (int i = 0; i < 8; i++) {
        publishSlotState(slots[i].name, slots[i].currentDist, true);
        delay(10);
    }
}

void handleSlotCmd(const String &slotId, const String &colorCmd) {
    // Kiểm tra xem slotId có thuộc hàng của board này không (ví dụ 'E' trong 'E1')
    if (slotId.length() < 2 || slotId.charAt(0) != ROW_CHAR) {
        return; // Không thuộc hàng của board này -> bỏ qua
    }

    int col = slotId.substring(1).toInt();
    if (col < 1 || col > 8) return;
    int localIdx = col - 1; // 0..7

    String c = colorCmd;
    c.trim();
    c.replace("\"", "");

    if (c.equalsIgnoreCase("RED")) {
        strip.setPixelColor(localIdx, strip.Color(255, 0, 0));
        slots[localIdx].currentState = "OCCUPIED";
    } else if (c.equalsIgnoreCase("GREEN")) {
        strip.setPixelColor(localIdx, strip.Color(0, 255, 0));
        slots[localIdx].currentState = "FREE";
    } else if (c.equalsIgnoreCase("YELLOW")) {
        strip.setPixelColor(localIdx, strip.Color(255, 200, 0));
        slots[localIdx].currentState = "OVERLAP";
    } else if (c.equalsIgnoreCase("OFF")) {
        strip.setPixelColor(localIdx, strip.Color(0, 0, 0));
    }
    strip.show();
    Serial.printf("[SLOT_LED][%s] Đặt LED ô %s (local pixel %d) -> %s\n",
                  DEVICE_ID, slotId.c_str(), localIdx, c.c_str());
}

void handleGuideCmd(const JsonDocument &doc) {
    String colorStr = doc["color"] | "GREEN";
    uint32_t c = strip.Color(0, 255, 0);
    if (colorStr.equalsIgnoreCase("CYAN") || colorStr.equalsIgnoreCase("#00E5FF")) {
        c = strip.Color(0, 229, 255);
    } else if (colorStr.equalsIgnoreCase("RED")) {
        c = strip.Color(255, 0, 0);
    } else if (colorStr.equalsIgnoreCase("YELLOW")) {
        c = strip.Color(255, 200, 0);
    }

    JsonArrayConst pathArr = doc["path"].as<JsonArrayConst>();
    bool matchedAny = false;

    for (int p : pathArr) {
        // Ánh xạ dải pixel toàn cục vào phạm vi cục bộ của board
        if (p >= PIXEL_OFFSET && p < PIXEL_OFFSET + PIXEL_COUNT) {
            int localPx = p - PIXEL_OFFSET;
            strip.setPixelColor(localPx, c);
            matchedAny = true;
        }
    }

    if (matchedAny) {
        strip.show();
        guideActive = true;
        guideExpireAt = millis() + (doc["ttl"] | 120) * 1000UL;
        Serial.printf("[GUIDE][%s] Đã bật dải LED dẫn đường cho các pixel thuộc phạm vi [%d..%d]\n",
                      DEVICE_ID, PIXEL_OFFSET, PIXEL_OFFSET + PIXEL_COUNT - 1);
    }
}

void updateGuideLed() {
    if (guideActive && millis() >= guideExpireAt) {
        guideActive = false;
        Serial.printf("[GUIDE][%s] Hết thời gian dẫn đường (TTL) -> Khôi phục màu đèn ô.\n", DEVICE_ID);
        for (int i = 0; i < 8; i++) {
            if (slots[i].currentState == "OCCUPIED") {
                strip.setPixelColor(i, strip.Color(255, 0, 0));
            } else if (slots[i].currentState == "OVERLAP") {
                strip.setPixelColor(i, strip.Color(255, 200, 0));
            } else {
                strip.setPixelColor(i, strip.Color(0, 255, 0));
            }
        }
        strip.show();
    }
}
#endif
