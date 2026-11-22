# Cach ly tin hieu HC-SR04 va ESP32

Chan `ECHO` cua HC-SR04 co the len muc 5 V, trong khi GPIO ESP32 chi chiu
toi da 3.3 V. Moi cam bien phai di qua mot cau phan ap truoc khi vao GPIO.

```text
HC-SR04 ECHO (5 V)
        |
       R1 = 1 kOhm
        |
        +-------------------- ESP32 GPIO ECHO
        |
       R2 = 2 kOhm
        |
       GND
```

Ap dung cau phan ap nay cho `sensor1` den `sensor12`, giu chung `TRIG` 3.3 V
tu GPIO23 va noi chung GND. Khong noi truc tiep chan `ECHO` vao ESP32.

| Cam bien | GPIO ESP32 |
|---|---:|
| sensor1 | GPIO13 |
| sensor2 | GPIO14 |
| sensor3 | GPIO16 |
| sensor4 | GPIO17 |
| sensor5 | GPIO18 |
| sensor6 | GPIO19 |
| sensor7 | GPIO36 (VP) |
| sensor8 | GPIO39 (VN) |
| sensor9 | GPIO25 |
| sensor10 | GPIO26 |
| sensor11 | GPIO27 |
| sensor12 | GPIO32 |

Ty le 1 kOhm/2 kOhm ha muc 5 V xuong khoang 3.33 V. Dat dien tro gan ESP32,
di day tin hieu ngan va bo sung tu 100 nF giua VCC-GND moi cum cam bien neu
cam bien thuc te bi nhiễu nguon.
