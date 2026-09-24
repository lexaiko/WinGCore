# GManager: arah runtime native Windows

Tanggal: 2026-09-24. Status: fondasi runtime dan check-in sudah diimplementasikan;
dua check-in live dengan ID yang sama setelah restart telah diverifikasi.
Enrollment akun, broker token, dan integrasi WPF sudah diimplementasikan. Pembuktian
live enrollment dan akses layanan masih menunggu login akun testing pengguna.
Lihat native-runtime-verification.md.

## Tujuan

GManager diarahkan menjadi runtime layanan Google di Windows yang mengambil pola
device identity, account manager, registrasi, dan token broker dari microG.
Runtime berjalan langsung di Windows tanpa ketergantungan wajib pada HP, ADB,
atau emulator. UI WPF mengelola runtime tersebut.

Native berarti implementasi lifecycle dan protokol berjalan di Windows. Ini tidak
berarti semua proses autentikasi bebas halaman web, consent, atau challenge Google.
Jalur autentikasi Android perlu diteliti tersendiri; keberhasilan OAuth desktop
yang ada tidak membuktikan kompatibilitas jalur tersebut.

## Kondisi kode saat ini

- Satu aplikasi WPF dengan DI, SQLite, enkripsi kolom, dan Windows PasswordVault.
- OAuth desktop dengan PKCE dan token cache per akun.
- Credential refresh token disimpan berdasarkan email.
- Gmail/Drive menggunakan API REST; scope login saat ini hanya identitas dasar.
- RuntimeHost terpisah menyediakan profil device persisten, IPC lokal,
  penyimpanan registrasi dan sesi terenkripsi, check-in Android, dan broker grant.
- WPF membuka login Android melalui WebView2 serta mengelola profil dan sesi native.
- Cache akun menggunakan ID sesi agar email yang sama pada dua device tetap terpisah.

Blueprint lama menjelaskan aplikasi akun berbasis OAuth. Dokumen ini mencatat
arah produk baru; daftar tahap di bawah membedakan implementasi dan bukti kelulusan.

## Pembagian komponen

| Project | Tanggung jawab |
| --- | --- |
| GManager | UI WPF; menampilkan status dan mengirim perintah |
| GManager.Contracts | Data dan pesan komunikasi lokal yang memiliki versi |
| GManager.Runtime | Lifecycle device, sesi akun, login ticket, dan grant cache |
| GManager.RuntimeHost | Proses background per user dan CLI diagnostik |
| GManager.Platform.Windows | Credential store, SQLite, IPC, integrasi Windows |
| GManager.Providers.Google | Check-in Protobuf dan autentikasi protokol Android |

Runtime menjadi pemilik tunggal penyimpanan dan state. UI menggunakan named pipe
yang dibatasi ke user Windows terkait. Akses token tidak disiarkan melalui event
UI; akses klien lain memerlukan otorisasi eksplisit dan ruang lingkup layanan.
Kegagalan UI tidak boleh menghapus sesi atau identitas device.

## Model data

- DeviceProfile: konfigurasi build/model yang diinginkan, versi profil, asal data.
- DeviceInstance: ID lokal persisten dan referensi versi profil yang digunakan.
- DeviceRegistration: hasil registrasi server, referensi credential rahasia,
  provider, waktu check-in, dan status; terikat pada device instance.
- Account: identitas akun yang terpisah dari perangkat.
- AccountSession: hubungan akun, device instance, provider autentikasi, dan status.
- ServiceGrant: sesi, layanan/audience, identitas klien, scope, masa berlaku,
  dan referensi credential.

ID lokal, ID hasil check-in, Android ID yang dilihat aplikasi, dan fingerprint
tidak diperlakukan sebagai identifier yang sama. ID yang seharusnya diterbitkan
server hanya disimpan dari respons yang tervalidasi. Mengubah profil tidak boleh
diam-diam menggunakan ulang registrasi lama; provider menentukan kebutuhan
registrasi ulang, dengan perubahan state yang terlihat oleh pengguna.

Credential diberi namespace menurut provider dan session/device ID, bukan email
saja. Ekspor profil tidak menyertakan token atau secret registrasi. Respons
autentikasi dan check-in tidak dicetak mentah ke log.

## Jalur provider

OAuthDesktop mempertahankan fungsi yang ada selama migrasi. AndroidGoogle menjadi
adapter terpisah yang ditambahkan setelah studi protokol. Token kedua provider
tidak dianggap dapat saling menggantikan. API Gmail/Drive tidak otomatis dapat
memakai token Android tanpa pembuktian audience dan izin yang sesuai.

Adapter eksperimental harus bisa melaporkan Unsupported, ChallengeRequired,
Rejected, atau TransientFailure. Jangan mengganti kegagalan dengan respons sukses
lokal, dan jangan menampilkan state Registered sebelum server mengonfirmasinya.

## Tahap implementasi dan bukti kelulusan

1. Studi protokol: pilih commit microG tertentu; dokumentasikan schema request dan
   response, lifecycle secret, dependensi Android, serta lisensi file yang dirujuk.
   Pin commit sebelum porting; jangan memakai branch bergerak sebagai spesifikasi.
2. Fondasi lokal: ekstrak runtime, model device/session, dan penyimpanan. Uji bahwa
   identitas bertahan setelah restart dan sesi antar-device tidak bertabrakan.
3. Check-in: buat eksperimen kecil native Windows, mulai dari fixture lokal untuk
   serialisasi/deserialisasi. Uji live secara terkontrol; catat penerimaan atau
   penolakan server. Lulus jika registrasi diterima dan check-in berikutnya memakai
   identitas persisten sesuai protokol. Mock hanya membuktikan perilaku lokal.
4. Account enrollment: buktikan satu akun uji dapat membentuk sesi menggunakan
   jalur Android yang diteliti, termasuk penyelesaian challenge oleh pengguna.
   Jangan menyimpan password akun sebagai mekanisme sesi permanen.
5. Token layanan: buktikan satu layanan dapat dipanggil memakai grant yang tepat;
   uji kedaluwarsa, pencabutan, dan isolasi sesi sebelum memperluas cakupan.
6. Integrasi WPF: tampilkan state runtime nyata dan hubungkan pengelolaan profil,
   akun, serta diagnostik. Messaging dan layanan tambahan menyusul per kebutuhan.

## Batas pembuktian

Check-in diterima, akun berhasil login, device muncul di halaman akun Google,
dan hasil Play Integrity adalah hasil yang berbeda dan harus diuji terpisah.
Tidak ada janji kompatibilitas penuh microG, dukungan APK, atau kelulusan integrity.
Pola konfigurasi profil dari PIF dapat menjadi referensi desain, tetapi mekanisme
Android-nya tidak diasumsikan tersedia di Windows.

## Referensi penelitian

- microG: https://github.com/microg/GmsCore
- Check-in: https://github.com/microg/GmsCore/blob/master/play-services-core/src/main/java/org/microg/gms/checkin/CheckinClient.java
- Auth: https://github.com/microg/GmsCore/tree/master/play-services-core/src/main/java/org/microg/gms/auth
- Integrity verdicts: https://developer.android.com/google/play/integrity/verdicts

Implementasi check-in mengunci commit c005d992d594b5008dd0ba08719c852a7ae8a6fb.
Lihat native-protocol-research.md untuk sumber, perbedaan implementasi, dan batas
pengujian. Kompatibilitas akun dan layanan lain belum dibuktikan.

## Batas IPC saat ini

Master credential hanya berada di runtime dan database DPAPI. Hasil login sekali
pakai dikirim oleh WPF ke runtime; grant berumur pendek dikirim kembali untuk API
Gmail/Drive. Pipe membatasi akses ke user Windows yang sama, bukan aplikasi tertentu.
Karena itu aplikasi lain yang berjalan sebagai user tersebut termasuk dalam batas
kepercayaan. Event dan diagnostik tidak memuat token. Menghapus sesi hanya mencabut
penyimpanan lokal; pengguna mengelola otorisasi server di akun Google.
