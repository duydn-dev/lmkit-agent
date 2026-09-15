import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_provider.dart';
import 'canvas_repository.dart';

final canvasRepositoryProvider = Provider<CanvasRepository>(
  (ref) => CanvasRepository(ref.watch(apiClientProvider)),
);
