import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../core/auth/auth_provider.dart';
import 'studio_repository.dart';

final studioRepositoryProvider = Provider<StudioRepository>(
  (ref) => StudioRepository(ref.watch(apiClientProvider)),
);
