/// Catalogue ngôn ngữ đích cho Dịch video — ISO 639-1 tĩnh (184 mã).
/// Mirror nội dung với Angular `video-languages.ts` và hai client lmkit-agent.
class VideoLanguage {
  final String code;
  final String nativeName;
  final String englishName;

  const VideoLanguage({
    required this.code,
    required this.nativeName,
    required this.englishName,
  });
}

const List<VideoLanguage> videoLanguages = [
  VideoLanguage(code: 'aa', nativeName: 'Afaraf', englishName: 'Afar'),
  VideoLanguage(code: 'ab', nativeName: 'аҧсуа', englishName: 'Abkhaz'),
  VideoLanguage(code: 'ae', nativeName: 'avesta', englishName: 'Avestan'),
  VideoLanguage(code: 'af', nativeName: 'Afrikaans', englishName: 'Afrikaans'),
  VideoLanguage(code: 'ak', nativeName: 'Akan', englishName: 'Akan'),
  VideoLanguage(code: 'am', nativeName: 'አማርኛ', englishName: 'Amharic'),
  VideoLanguage(code: 'an', nativeName: 'Aragonés', englishName: 'Aragonese'),
  VideoLanguage(code: 'ar', nativeName: 'العربية', englishName: 'Arabic'),
  VideoLanguage(code: 'as', nativeName: 'অসমীয়া', englishName: 'Assamese'),
  VideoLanguage(code: 'av', nativeName: 'авар мацӀ', englishName: 'Avaric'),
  VideoLanguage(code: 'ay', nativeName: 'aymar aru', englishName: 'Aymara'),
  VideoLanguage(code: 'az', nativeName: 'azərbaycan dili', englishName: 'Azerbaijani'),
  VideoLanguage(code: 'ba', nativeName: 'башҡорт теле', englishName: 'Bashkir'),
  VideoLanguage(code: 'be', nativeName: 'Беларуская', englishName: 'Belarusian'),
  VideoLanguage(code: 'bg', nativeName: 'български език', englishName: 'Bulgarian'),
  VideoLanguage(code: 'bh', nativeName: 'बिहारी', englishName: 'Bihari'),
  VideoLanguage(code: 'bi', nativeName: 'Bislama', englishName: 'Bislama'),
  VideoLanguage(code: 'bm', nativeName: 'bamanankan', englishName: 'Bambara'),
  VideoLanguage(code: 'bn', nativeName: 'বাংলা', englishName: 'Bengali'),
  VideoLanguage(code: 'bo', nativeName: 'བོད་ཡིག', englishName: 'Tibetan'),
  VideoLanguage(code: 'br', nativeName: 'brezhoneg', englishName: 'Breton'),
  VideoLanguage(code: 'bs', nativeName: 'bosanski jezik', englishName: 'Bosnian'),
  VideoLanguage(code: 'ca', nativeName: 'català', englishName: 'Catalan'),
  VideoLanguage(code: 'ce', nativeName: 'нохчийн мотт', englishName: 'Chechen'),
  VideoLanguage(code: 'ch', nativeName: 'Chamoru', englishName: 'Chamorro'),
  VideoLanguage(code: 'co', nativeName: 'corsu', englishName: 'Corsican'),
  VideoLanguage(code: 'cr', nativeName: 'ᓀᐦᐃᔭᐍᐏᐣ', englishName: 'Cree'),
  VideoLanguage(code: 'cs', nativeName: 'čeština', englishName: 'Czech'),
  VideoLanguage(code: 'cu', nativeName: 'ѩзыкъ словѣньскъ', englishName: 'Old Church Slavonic'),
  VideoLanguage(code: 'cv', nativeName: 'чӑваш чӗлхи', englishName: 'Chuvash'),
  VideoLanguage(code: 'cy', nativeName: 'Cymraeg', englishName: 'Welsh'),
  VideoLanguage(code: 'da', nativeName: 'dansk', englishName: 'Danish'),
  VideoLanguage(code: 'de', nativeName: 'Deutsch', englishName: 'German'),
  VideoLanguage(code: 'dv', nativeName: 'ދިވެހި', englishName: 'Divehi'),
  VideoLanguage(code: 'dz', nativeName: 'རྫོང་ཁ', englishName: 'Dzongkha'),
  VideoLanguage(code: 'ee', nativeName: 'Eʋegbe', englishName: 'Ewe'),
  VideoLanguage(code: 'el', nativeName: 'Ελληνικά', englishName: 'Greek'),
  VideoLanguage(code: 'en', nativeName: 'English', englishName: 'English'),
  VideoLanguage(code: 'eo', nativeName: 'Esperanto', englishName: 'Esperanto'),
  VideoLanguage(code: 'es', nativeName: 'español', englishName: 'Spanish'),
  VideoLanguage(code: 'et', nativeName: 'eesti', englishName: 'Estonian'),
  VideoLanguage(code: 'eu', nativeName: 'euskara', englishName: 'Basque'),
  VideoLanguage(code: 'fa', nativeName: 'فارسی', englishName: 'Persian'),
  VideoLanguage(code: 'ff', nativeName: 'Fulfulde', englishName: 'Fula'),
  VideoLanguage(code: 'fi', nativeName: 'suomi', englishName: 'Finnish'),
  VideoLanguage(code: 'fj', nativeName: 'vosa Vakaviti', englishName: 'Fijian'),
  VideoLanguage(code: 'fo', nativeName: 'føroyskt', englishName: 'Faroese'),
  VideoLanguage(code: 'fr', nativeName: 'français', englishName: 'French'),
  VideoLanguage(code: 'fy', nativeName: 'Frysk', englishName: 'Western Frisian'),
  VideoLanguage(code: 'ga', nativeName: 'Gaeilge', englishName: 'Irish'),
  VideoLanguage(code: 'gd', nativeName: 'Gàidhlig', englishName: 'Scottish Gaelic'),
  VideoLanguage(code: 'gl', nativeName: 'galego', englishName: 'Galician'),
  VideoLanguage(code: 'gn', nativeName: 'Avañeʼẽ', englishName: 'Guaraní'),
  VideoLanguage(code: 'gu', nativeName: 'ગુજરાતી', englishName: 'Gujarati'),
  VideoLanguage(code: 'gv', nativeName: 'Gaelg', englishName: 'Manx'),
  VideoLanguage(code: 'ha', nativeName: 'Hausa', englishName: 'Hausa'),
  VideoLanguage(code: 'he', nativeName: 'עברית', englishName: 'Hebrew'),
  VideoLanguage(code: 'hi', nativeName: 'हिन्दी', englishName: 'Hindi'),
  VideoLanguage(code: 'ho', nativeName: 'Hiri Motu', englishName: 'Hiri Motu'),
  VideoLanguage(code: 'hr', nativeName: 'hrvatski', englishName: 'Croatian'),
  VideoLanguage(code: 'ht', nativeName: 'Kreyòl ayisyen', englishName: 'Haitian'),
  VideoLanguage(code: 'hu', nativeName: 'magyar', englishName: 'Hungarian'),
  VideoLanguage(code: 'hy', nativeName: 'Հայերեն', englishName: 'Armenian'),
  VideoLanguage(code: 'hz', nativeName: 'Otjiherero', englishName: 'Herero'),
  VideoLanguage(code: 'ia', nativeName: 'Interlingua', englishName: 'Interlingua'),
  VideoLanguage(code: 'id', nativeName: 'Bahasa Indonesia', englishName: 'Indonesian'),
  VideoLanguage(code: 'ie', nativeName: 'Interlingue', englishName: 'Interlingue'),
  VideoLanguage(code: 'ig', nativeName: 'Igbo', englishName: 'Igbo'),
  VideoLanguage(code: 'ii', nativeName: 'ꆈꌠ꒿', englishName: 'Nuosu'),
  VideoLanguage(code: 'ik', nativeName: 'Iñupiaq', englishName: 'Inupiaq'),
  VideoLanguage(code: 'io', nativeName: 'Ido', englishName: 'Ido'),
  VideoLanguage(code: 'is', nativeName: 'íslenska', englishName: 'Icelandic'),
  VideoLanguage(code: 'it', nativeName: 'italiano', englishName: 'Italian'),
  VideoLanguage(code: 'iu', nativeName: 'ᐃᓄᒃᑎᑐᑦ', englishName: 'Inuktitut'),
  VideoLanguage(code: 'ja', nativeName: '日本語', englishName: 'Japanese'),
  VideoLanguage(code: 'jv', nativeName: 'basa Jawa', englishName: 'Javanese'),
  VideoLanguage(code: 'ka', nativeName: 'ქართული', englishName: 'Georgian'),
  VideoLanguage(code: 'kg', nativeName: 'KiKongo', englishName: 'Kongo'),
  VideoLanguage(code: 'ki', nativeName: 'Gĩkũyũ', englishName: 'Kikuyu'),
  VideoLanguage(code: 'kj', nativeName: 'Kuanyama', englishName: 'Kwanyama'),
  VideoLanguage(code: 'kk', nativeName: 'Қазақ тілі', englishName: 'Kazakh'),
  VideoLanguage(code: 'kl', nativeName: 'kalaallisut', englishName: 'Kalaallisut'),
  VideoLanguage(code: 'km', nativeName: 'ខ្មែរ', englishName: 'Khmer'),
  VideoLanguage(code: 'kn', nativeName: 'ಕನ್ನಡ', englishName: 'Kannada'),
  VideoLanguage(code: 'ko', nativeName: '한국어', englishName: 'Korean'),
  VideoLanguage(code: 'kr', nativeName: 'Kanuri', englishName: 'Kanuri'),
  VideoLanguage(code: 'ks', nativeName: 'कश्मीरी', englishName: 'Kashmiri'),
  VideoLanguage(code: 'ku', nativeName: 'Kurdî', englishName: 'Kurdish'),
  VideoLanguage(code: 'kv', nativeName: 'коми кыв', englishName: 'Komi'),
  VideoLanguage(code: 'kw', nativeName: 'kernewek', englishName: 'Cornish'),
  VideoLanguage(code: 'ky', nativeName: 'кыргыз тили', englishName: 'Kyrgyz'),
  VideoLanguage(code: 'la', nativeName: 'latine', englishName: 'Latin'),
  VideoLanguage(code: 'lb', nativeName: 'Lëtzebuergesch', englishName: 'Luxembourgish'),
  VideoLanguage(code: 'lg', nativeName: 'Luganda', englishName: 'Ganda'),
  VideoLanguage(code: 'li', nativeName: 'Limburgs', englishName: 'Limburgish'),
  VideoLanguage(code: 'ln', nativeName: 'Lingála', englishName: 'Lingala'),
  VideoLanguage(code: 'lo', nativeName: 'ພາສາລາວ', englishName: 'Lao'),
  VideoLanguage(code: 'lt', nativeName: 'lietuvių kalba', englishName: 'Lithuanian'),
  VideoLanguage(code: 'lu', nativeName: 'Kiluba', englishName: 'Luba-Katanga'),
  VideoLanguage(code: 'lv', nativeName: 'latviešu valoda', englishName: 'Latvian'),
  VideoLanguage(code: 'mg', nativeName: 'Malagasy fiteny', englishName: 'Malagasy'),
  VideoLanguage(code: 'mh', nativeName: 'Kajin M̧ajeļ', englishName: 'Marshallese'),
  VideoLanguage(code: 'mi', nativeName: 'te reo Māori', englishName: 'Māori'),
  VideoLanguage(code: 'mk', nativeName: 'македонски јазик', englishName: 'Macedonian'),
  VideoLanguage(code: 'ml', nativeName: 'മലയാളം', englishName: 'Malayalam'),
  VideoLanguage(code: 'mn', nativeName: 'монгол', englishName: 'Mongolian'),
  VideoLanguage(code: 'mr', nativeName: 'मराठी', englishName: 'Marathi'),
  VideoLanguage(code: 'ms', nativeName: 'bahasa Melayu', englishName: 'Malay'),
  VideoLanguage(code: 'mt', nativeName: 'Malti', englishName: 'Maltese'),
  VideoLanguage(code: 'my', nativeName: 'ဗမာစာ', englishName: 'Burmese'),
  VideoLanguage(code: 'na', nativeName: 'Ekakairũ Naoero', englishName: 'Nauru'),
  VideoLanguage(code: 'nb', nativeName: 'Norsk bokmål', englishName: 'Norwegian Bokmål'),
  VideoLanguage(code: 'nd', nativeName: 'isiNdebele', englishName: 'Northern Ndebele'),
  VideoLanguage(code: 'ne', nativeName: 'नेपाली', englishName: 'Nepali'),
  VideoLanguage(code: 'ng', nativeName: 'Owambo', englishName: 'Ndonga'),
  VideoLanguage(code: 'nl', nativeName: 'Nederlands', englishName: 'Dutch'),
  VideoLanguage(code: 'nn', nativeName: 'Norsk nynorsk', englishName: 'Norwegian Nynorsk'),
  VideoLanguage(code: 'no', nativeName: 'Norsk', englishName: 'Norwegian'),
  VideoLanguage(code: 'nr', nativeName: 'isiNdebele', englishName: 'Southern Ndebele'),
  VideoLanguage(code: 'nv', nativeName: 'Diné bizaad', englishName: 'Navajo'),
  VideoLanguage(code: 'ny', nativeName: 'chiChewa', englishName: 'Chichewa'),
  VideoLanguage(code: 'oc', nativeName: 'occitan', englishName: 'Occitan'),
  VideoLanguage(code: 'oj', nativeName: 'ᐊᓂᔑᓈᐯᒧᐎᓐ', englishName: 'Ojibwe'),
  VideoLanguage(code: 'om', nativeName: 'Afaan Oromoo', englishName: 'Oromo'),
  VideoLanguage(code: 'or', nativeName: 'ଓଡ଼ିଆ', englishName: 'Oriya'),
  VideoLanguage(code: 'os', nativeName: 'ирон æвзаг', englishName: 'Ossetian'),
  VideoLanguage(code: 'pa', nativeName: 'ਪੰਜਾਬੀ', englishName: 'Panjabi'),
  VideoLanguage(code: 'pi', nativeName: 'पाऴि', englishName: 'Pāli'),
  VideoLanguage(code: 'pl', nativeName: 'polski', englishName: 'Polish'),
  VideoLanguage(code: 'ps', nativeName: 'پښتو', englishName: 'Pashto'),
  VideoLanguage(code: 'pt', nativeName: 'português', englishName: 'Portuguese'),
  VideoLanguage(code: 'qu', nativeName: 'Runa Simi', englishName: 'Quechua'),
  VideoLanguage(code: 'rm', nativeName: 'rumantsch grischun', englishName: 'Romansh'),
  VideoLanguage(code: 'rn', nativeName: 'kiRundi', englishName: 'Kirundi'),
  VideoLanguage(code: 'ro', nativeName: 'română', englishName: 'Romanian'),
  VideoLanguage(code: 'ru', nativeName: 'русский язык', englishName: 'Russian'),
  VideoLanguage(code: 'rw', nativeName: 'Ikinyarwanda', englishName: 'Kinyarwanda'),
  VideoLanguage(code: 'sa', nativeName: 'संस्कृतम्', englishName: 'Sanskrit'),
  VideoLanguage(code: 'sc', nativeName: 'sardu', englishName: 'Sardinian'),
  VideoLanguage(code: 'sd', nativeName: 'सिन्धी', englishName: 'Sindhi'),
  VideoLanguage(code: 'se', nativeName: 'Davvisámegiella', englishName: 'Northern Sami'),
  VideoLanguage(code: 'sg', nativeName: 'yângâ tî sängö', englishName: 'Sango'),
  VideoLanguage(code: 'si', nativeName: 'සිංහල', englishName: 'Sinhala'),
  VideoLanguage(code: 'sk', nativeName: 'slovenčina', englishName: 'Slovak'),
  VideoLanguage(code: 'sl', nativeName: 'slovenščina', englishName: 'Slovenian'),
  VideoLanguage(code: 'sm', nativeName: 'gagana faa Samoa', englishName: 'Samoan'),
  VideoLanguage(code: 'sn', nativeName: 'chiShona', englishName: 'Shona'),
  VideoLanguage(code: 'so', nativeName: 'Soomaaliga', englishName: 'Somali'),
  VideoLanguage(code: 'sq', nativeName: 'shqip', englishName: 'Albanian'),
  VideoLanguage(code: 'sr', nativeName: 'српски језик', englishName: 'Serbian'),
  VideoLanguage(code: 'ss', nativeName: 'SiSwati', englishName: 'Swati'),
  VideoLanguage(code: 'st', nativeName: 'Sesotho', englishName: 'Southern Sotho'),
  VideoLanguage(code: 'su', nativeName: 'Basa Sunda', englishName: 'Sundanese'),
  VideoLanguage(code: 'sv', nativeName: 'svenska', englishName: 'Swedish'),
  VideoLanguage(code: 'sw', nativeName: 'Kiswahili', englishName: 'Swahili'),
  VideoLanguage(code: 'ta', nativeName: 'தமிழ்', englishName: 'Tamil'),
  VideoLanguage(code: 'te', nativeName: 'తెలుగు', englishName: 'Telugu'),
  VideoLanguage(code: 'tg', nativeName: 'тоҷикӣ', englishName: 'Tajik'),
  VideoLanguage(code: 'th', nativeName: 'ไทย', englishName: 'Thai'),
  VideoLanguage(code: 'ti', nativeName: 'ትግርኛ', englishName: 'Tigrinya'),
  VideoLanguage(code: 'tk', nativeName: 'Türkmen', englishName: 'Turkmen'),
  VideoLanguage(code: 'tl', nativeName: 'Wikang Tagalog', englishName: 'Tagalog'),
  VideoLanguage(code: 'tn', nativeName: 'Setswana', englishName: 'Tswana'),
  VideoLanguage(code: 'to', nativeName: 'faka Tonga', englishName: 'Tonga'),
  VideoLanguage(code: 'tr', nativeName: 'Türkçe', englishName: 'Turkish'),
  VideoLanguage(code: 'ts', nativeName: 'Xitsonga', englishName: 'Tsonga'),
  VideoLanguage(code: 'tt', nativeName: 'татарча', englishName: 'Tatar'),
  VideoLanguage(code: 'tw', nativeName: 'Twi', englishName: 'Twi'),
  VideoLanguage(code: 'ty', nativeName: 'Reo Tahiti', englishName: 'Tahitian'),
  VideoLanguage(code: 'ug', nativeName: 'Uyƣurqə', englishName: 'Uyghur'),
  VideoLanguage(code: 'uk', nativeName: 'українська', englishName: 'Ukrainian'),
  VideoLanguage(code: 'ur', nativeName: 'اردو', englishName: 'Urdu'),
  VideoLanguage(code: 'uz', nativeName: 'Ozbek', englishName: 'Uzbek'),
  VideoLanguage(code: 've', nativeName: 'Tshivenḓa', englishName: 'Venda'),
  VideoLanguage(code: 'vi', nativeName: 'Tiếng Việt', englishName: 'Vietnamese'),
  VideoLanguage(code: 'vo', nativeName: 'Volapük', englishName: 'Volapük'),
  VideoLanguage(code: 'wa', nativeName: 'walon', englishName: 'Walloon'),
  VideoLanguage(code: 'wo', nativeName: 'Wollof', englishName: 'Wolof'),
  VideoLanguage(code: 'xh', nativeName: 'isiXhosa', englishName: 'Xhosa'),
  VideoLanguage(code: 'yi', nativeName: 'ייִדיש', englishName: 'Yiddish'),
  VideoLanguage(code: 'yo', nativeName: 'Yorùbá', englishName: 'Yoruba'),
  VideoLanguage(code: 'za', nativeName: 'Saɯ cueŋƅ', englishName: 'Zhuang'),
  VideoLanguage(code: 'zh', nativeName: '中文', englishName: 'Chinese'),
  VideoLanguage(code: 'zu', nativeName: 'isiZulu', englishName: 'Zulu'),
];

/// Chuẩn hoá để tìm kiếm không phân biệt hoa thường/dấu.
String normalizeVideoSearch(String value) {
  final lower = value.toLowerCase();
  // Bóc dấu Latin bằng cách loại bỏ combining marks sau NFD.
  // Dart không có normalize NFD sẵn nên dùng bảng thay thế thủ công cho tiếng Việt.
  const withDia =
      'àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđñç';
  const withoutDia =
      'aaaaaaaaaaaaaaaaaeeeeeeeeeeeiiiiiiooooooooooooooooouuuuuuuuuuuyyyyydnc';
  var out = lower;
  for (var i = 0; i < withDia.length; i++) {
    out = out.replaceAll(withDia[i], withoutDia[i]);
  }
  // Loại combining diacritical marks còn sót (cho các ngôn ngữ khác).
  out = out.replaceAll(RegExp(r'[\u0300-\u036f]'), '');
  return out;
}

/// Tìm locale theo mã, tên bản địa hoặc tên tiếng Anh.
List<VideoLanguage> filterVideoLanguages(String query) {
  final q = normalizeVideoSearch(query.trim());
  if (q.isEmpty) return videoLanguages;
  return videoLanguages.where((lang) {
    return normalizeVideoSearch(lang.code).contains(q) ||
        normalizeVideoSearch(lang.nativeName).contains(q) ||
        normalizeVideoSearch(lang.englishName).contains(q);
  }).toList();
}

/// Chỉ chấp nhận URL tuyệt đối http/https — kiểm tra cú pháp, không fetch.
bool isAllowedVideoSource(String value) {
  final trimmed = value.trim();
  if (trimmed.isEmpty) return false;
  final uri = Uri.tryParse(trimmed);
  if (uri == null || !uri.isAbsolute) return false;
  return uri.scheme == 'http' || uri.scheme == 'https';
}

/// Nhận diện URL trang mạng xã hội (không hứa preview chắc chắn).
bool isSocialPageUrl(String value) {
  final lower = value.trim().toLowerCase();
  return lower.contains('facebook.com') ||
      lower.contains('fb.watch') ||
      lower.contains('youtube.com') ||
      lower.contains('youtu.be');
}
