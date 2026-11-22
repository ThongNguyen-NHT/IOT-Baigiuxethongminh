"""
Script dọn dẹp toàn bộ tin nhắn MQTT Retained trên Broker (HiveMQ hoặc tùy chọn).
Cách thức: Subscribe wildcard {prefix}/#, thu thập các topic có cờ retain=True,
và publish payload rỗng (zero-length) kèm retain=True để broker xóa vĩnh viễn.

Sử dụng:
  python tools/clean_retained_mqtt.py [--broker broker.hivemq.com] [--port 1883] [--prefix dacn-smartparking/lot1] [--all]
"""

import sys
import time
import argparse
import paho.mqtt.client as mqtt

def clean_retained(broker, port, prefix, clear_all=False):
    retained_topics = set()
    topic_filter = f"{prefix.rstrip('/')}/#"

    print(f"[*] Kết nối tới MQTT Broker: {broker}:{port}")
    print(f"[*] Quét các topic có tin Retained dưới nhánh: {topic_filter}...")

    def on_connect(client, userdata, flags, rc):
        if rc == 0:
            print("[+] Đã kết nối MQTT Broker thành công. Đang quét...")
            client.subscribe(topic_filter, qos=1)
        else:
            print(f"[-] Lỗi kết nối MQTT: {rc}")

    def on_message(client, userdata, msg):
        if msg.retain:
            topic = msg.topic
            retained_topics.add(topic)
            payload_str = msg.payload.decode('utf-8', errors='ignore')
            if len(payload_str) > 80:
                payload_str = payload_str[:77] + "..."
            print(f"  -> Phát hiện RETAINED: [{topic}] => {payload_str}")

    # Initialize client (compatible with paho-mqtt 1.x and 2.x)
    try:
        client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION1, "cleaner_scanner")
    except Exception:
        client = mqtt.Client("cleaner_scanner")

    client.on_connect = on_connect
    client.on_message = on_message

    client.connect(broker, port, 60)
    client.loop_start()
    
    # Chờ 3 giây để broker đẩy về toàn bộ retained messages
    time.sleep(3.5)
    client.loop_stop()
    client.disconnect()

    if not retained_topics:
        print("[+] Không tìm thấy tin nhắn Retained nào cần dọn dẹp!")
        return

    print(f"\n[*] Tìm thấy tổng cộng {len(retained_topics)} topic có Retained.")
    
    # Định nghĩa các topic HỢP LỆ được phép giữ retained theo đặc tả
    allowed_patterns = [
        "/state",         # slot/{id}/state
        "/status",        # device/{id}/status
        "/power",         # device/{id}/power
        "/summary"        # summary
    ]

    topics_to_clear = []
    for t in sorted(retained_topics):
        if clear_all:
            topics_to_clear.append(t)
        else:
            # Nếu không phải topic trạng thái được phép, thì phải xóa
            is_allowed = any(t.endswith(pat) for pat in allowed_patterns)
            if not is_allowed:
                topics_to_clear.append(t)
            else:
                print(f"  [GIỮ NGUYÊN - Trạng thái hợp lệ]: {t}")

    if not topics_to_clear:
        print("[+] Toàn bộ topic Retained hiện tại đều hợp lệ theo hợp đồng (state, status, power, summary).")
        return

    print(f"\n[*] Bắt đầu xóa {len(topics_to_clear)} topic Retained không hợp lệ/lệnh cũ...")
    
    try:
        cleaner = mqtt.Client(mqtt.CallbackAPIVersion.VERSION1, "cleaner_remover")
    except Exception:
        cleaner = mqtt.Client("cleaner_remover")

    cleaner.connect(broker, port, 60)
    cleaner.loop_start()

    for topic in topics_to_clear:
        # Publish empty payload with retain=True to delete from broker
        info = cleaner.publish(topic, payload="", qos=1, retain=True)
        info.wait_for_publish(timeout=2.0)
        print(f"  [ĐÃ XÓA RETAINED] {topic}")

    time.sleep(1.0)
    cleaner.loop_stop()
    cleaner.disconnect()
    print("[+] Hoàn tất dọn dẹp tin nhắn Retained!")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Dọn dẹp MQTT Retained messages")
    parser.add_argument("--broker", default="broker.hivemq.com", help="MQTT Broker host")
    parser.add_argument("--port", type=int, default=1883, help="MQTT Broker port")
    parser.add_argument("--prefix", default="dacn-smartparking/lot1", help="Topic prefix")
    parser.add_argument("--all", action="store_true", help="Xóa TẤT CẢ retained kể cả state (dùng khi reset triệt để)")
    args = parser.parse_args()

    clean_retained(args.broker, args.port, args.prefix, clear_all=args.all)
