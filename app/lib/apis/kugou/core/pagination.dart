// ArchoeraMvsync UI
// Copyright (C) 2026 Archoera && BetaStudio2
// SPDX-License-Identifier: AGPL-3.0-or-later

/// KG 分页聚合——对齐 apis/kugou/core/pagination.ts。
///
/// 已知总数时按 3 页并发拉取并校验完整性；未知总数时顺序拉取到短页。
/// 任一页失败直接向上抛错（不做静默截断）。
library;

/// 单页适配结果：[total] 为 null 表示接口未给总数
typedef KgPage<T> = ({List<T> items, int? total});

/// 拉取完整列表
Future<({List<T> items, int total})> kgFetchAllPages<T>(
  Future<KgPage<T>> Function(int page) requestPage,
  int pageSize,
) async {
  final first = await requestPage(1);
  final items = [...first.items];

  final total = first.total;
  if (total == null) {
    var page = 1;
    var pageLength = first.items.length;
    while (pageLength == pageSize) {
      final next = await requestPage(++page);
      items.addAll(next.items);
      pageLength = next.items.length;
    }
    return (items: items, total: items.length);
  }

  final pageCount = (total / pageSize).ceil();
  for (var page = 2; page <= pageCount; page += 3) {
    final batch = <Future<KgPage<T>>>[
      for (var i = 0; i < 3 && page + i <= pageCount; i++)
        requestPage(page + i),
    ];
    for (final result in await Future.wait(batch)) {
      items.addAll(result.items);
    }
  }

  if (items.length < total) {
    throw StateError('KG 列表数据不完整，请重试');
  }
  return (items: items.sublist(0, total), total: total);
}
