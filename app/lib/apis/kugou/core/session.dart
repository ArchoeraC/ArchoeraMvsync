// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 会话 / 安装级设备身份（宿主注入的 [SessionStore]）——对齐
/// apis/kugou/index.ts 的 `getKugouSession` / `mergeKugouSession`。
///
/// 存储键与 `services/kugou` 保持一致：
/// - `kugou`：登录会话（token / userid / nickname / avatarUrl）；
/// - `kugou_device`：设备身份（guid / mid / dev / mac / webgl / dfid）。
///
/// 两层共用同一份设备身份，避免同一安装出现两套指纹（风控敏感）。
library;

import '../../runtime.dart';
import 'crypto.dart';

/// 登录会话在宿主会话存储中的平台键
const kgSessionPlatform = 'kugou';

/// 安装级设备身份在宿主会话存储中的平台键
const kgDevicePlatform = 'kugou_device';

/// 当前 KG 会话（未登录返回空 Map）
Map<String, String> kgSession() =>
    getRuntime().sessionStore.get(kgSessionPlatform);

/// 当前安装级设备身份（首次运行可能为空）
Map<String, String> kgDevice() =>
    getRuntime().sessionStore.get(kgDevicePlatform);

/// 是否已登录（token + userid 均非空）
bool get kgLoggedIn {
  final s = kgSession();
  return (s['token'] ?? '').isNotEmpty && (s['userid'] ?? '').isNotEmpty;
}

/// 设备 MID：设备记录优先，其次由 GUID 推导（保证同一安装长期稳定）
String kgMid() {
  final device = kgDevice();
  final mid = (device['mid'] ?? '').trim();
  if (mid.isNotEmpty) return mid;
  final guid = (device['guid'] ?? '').trim();
  return kgCalcMid(guid.isEmpty ? 'archoera-kugou-device-mid' : guid);
}

/// 设备 dfid：未注册时返回空串（调用方用 `-` 兜底或先注册）
String kgDfid() => (kgDevice()['dfid'] ?? '').trim();

/// 登录 token（未登录为空串）
String kgToken() => (kgSession()['token'] ?? '').trim();

/// 登录 userid（未登录返回 `0`）
int kgUserId() => int.tryParse((kgSession()['userid'] ?? '').trim()) ?? 0;

/// 合并写入登录会话
void kgSaveSession(Map<String, String> values) {
  final store = getRuntime().sessionStore;
  store.save(kgSessionPlatform, {...store.get(kgSessionPlatform), ...values});
}

/// 合并写入安装级设备身份
void kgSaveDevice(Map<String, String> values) {
  final store = getRuntime().sessionStore;
  store.save(kgDevicePlatform, {...store.get(kgDevicePlatform), ...values});
}

/// 退出登录：保留设备身份（guid / dfid），清空 token/userid 与资料
void kgClearSession() {
  final store = getRuntime().sessionStore;
  final device = store.get(kgDevicePlatform);
  store.clear(kgSessionPlatform);
  if (device.isNotEmpty) store.save(kgDevicePlatform, device);
}
