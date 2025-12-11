-- ============================================================
-- RESET TOÀN BỘ DATABASE - CHỈ VECTOR MATCHING (pgvector)
-- ============================================================

SET search_path = public;

-- Drop tables (old → new)
DROP TABLE IF EXISTS whitelist_unit CASCADE;
DROP TABLE IF EXISTS duplicate_detection CASCADE;
DROP TABLE IF EXISTS match_result CASCADE;

-- legacy hash/sig tables: bỏ hoàn toàn
DROP TABLE IF EXISTS code_embedding CASCADE;
DROP TABLE IF EXISTS code_signature CASCADE;    -- legacy
DROP TABLE IF EXISTS code_fingerprint CASCADE;  -- legacy
DROP TABLE IF EXISTS code_unit CASCADE;
DROP TABLE IF EXISTS code_file CASCADE;
DROP TABLE IF EXISTS submission CASCADE;
DROP TABLE IF EXISTS examiner CASCADE;
DROP TABLE IF EXISTS exam CASCADE;

-- Drop legacy function
DROP FUNCTION IF EXISTS hamming_distance(bigint, bigint);

-- ============================================================
-- EXTENSIONS
-- ============================================================
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
CREATE EXTENSION IF NOT EXISTS vector;

-- ============================================================
-- 1) DANH MỤC
-- ============================================================
CREATE TABLE exam (
  exam_id     uuid PRIMARY KEY DEFAULT uuid_generate_v4(),
  code        text UNIQUE NOT NULL,
  title       text,
  created_at  timestamptz DEFAULT now()
);

CREATE TABLE examiner (
  examiner_id uuid PRIMARY KEY DEFAULT uuid_generate_v4(),
  code        text UNIQUE NOT NULL,
  name        text NOT NULL,
  created_at  timestamptz DEFAULT now()
);

-- ============================================================
-- 2) SUBMISSION & CẤU TRÚC MÃ NGUỒN
-- ============================================================
CREATE TABLE submission (
  submission_id  uuid PRIMARY KEY DEFAULT uuid_generate_v4(),
  exam_id        uuid NOT NULL REFERENCES exam(exam_id),
  student_id     uuid NOT NULL,
  examiner_id    uuid REFERENCES examiner(examiner_id),
  storage_key    text NOT NULL,
  status         text DEFAULT 'processed',
  total_files    int  DEFAULT 0,
  total_lines    int  DEFAULT 0,
  created_at     timestamptz DEFAULT now(),
  analyzed_at    timestamptz
);

CREATE UNIQUE INDEX uq_submission_exam_student
  ON submission(exam_id, student_id);

CREATE INDEX idx_submission_exam_examiner
  ON submission(exam_id, examiner_id);

CREATE TABLE code_file (
  file_id       uuid PRIMARY KEY DEFAULT uuid_generate_v4(),
  submission_id uuid NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
  rel_path      text NOT NULL,
  language      text,
  line_count    int,
  file_hash     bytea,        -- optional: giữ để kiểm soát integrity file (không dùng đối sánh)
  file_size     bigint,
  created_at    timestamptz DEFAULT now()
);

CREATE INDEX idx_file_submission ON code_file(submission_id);
CREATE INDEX idx_file_relpath   ON code_file(rel_path);

CREATE TABLE code_unit (
  unit_id       uuid PRIMARY KEY DEFAULT uuid_generate_v4(),
  submission_id uuid NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
  file_id       uuid NOT NULL REFERENCES code_file(file_id) ON DELETE CASCADE,
  unit_kind     text NOT NULL,            -- e.g., class/method/function
  unit_key      text NOT NULL,            -- định danh logic (tên hàm + namespace + path)
  start_line    int,
  end_line      int,
  content       text,
  content_hash  bytea,                    -- optional: có thể giữ để kiểm soát thay đổi, không dùng matching
  created_at    timestamptz DEFAULT now()
);

CREATE INDEX idx_unit_submission ON code_unit(submission_id);
CREATE INDEX idx_unit_file       ON code_unit(file_id);
CREATE INDEX idx_unit_key        ON code_unit(unit_key);

-- ============================================================
-- 3) VECTOR EMBEDDING (ONLY)
-- ============================================================
-- Chỉ lưu embedding + metadata để đối sánh ANN
-- Lưu ý: emb dùng cosine là phổ biến cho semantic similarity
CREATE TABLE code_embedding (
  unit_id              uuid PRIMARY KEY REFERENCES code_unit(unit_id) ON DELETE CASCADE,
  submission_id        uuid NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
  emb                  vector(1024) NOT NULL,  -- đảm bảo cùng dimension với model
  model_name           text NOT NULL,
  embedding_dimension  int NOT NULL CHECK (embedding_dimension = 1024),
  created_at           timestamptz DEFAULT now()
);

CREATE INDEX idx_emb_submission ON code_embedding(submission_id);

-- ANN index: ivfflat + cosine
-- Yêu cầu: ANALYZE bảng trước khi tạo ivfflat để optimizer chọn lists hợp lý
CREATE INDEX IF NOT EXISTS idx_emb_ann
  ON code_embedding USING ivfflat (emb vector_cosine_ops) WITH (lists = 100);

-- ============================================================
-- 4) KẾT QUẢ SO KHỚP & DUPLICATE (VECTOR ONLY)
-- ============================================================
CREATE TABLE match_result (
  id                 bigserial PRIMARY KEY,
  exam_id            uuid NOT NULL REFERENCES exam(exam_id),
  examiner_id        uuid REFERENCES examiner(examiner_id),
  src_unit_id        uuid NOT NULL REFERENCES code_unit(unit_id) ON DELETE CASCADE,
  tgt_unit_id        uuid NOT NULL REFERENCES code_unit(unit_id) ON DELETE CASCADE,
  src_submission_id  uuid NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
  tgt_submission_id  uuid NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
  method             text NOT NULL DEFAULT 'vector',
  score              numeric(6,5) NOT NULL,  -- cosine similarity (0..1)
  distance           numeric(6,5),           -- optional: 1 - cosine
  detail             jsonb,                  -- lưu k-NN params (k, ef_search, lists, threshold…)
  created_at         timestamptz DEFAULT now(),
  CONSTRAINT chk_match_src_ne_tgt CHECK (src_unit_id <> tgt_unit_id)
);

CREATE INDEX idx_match_exam_examiner ON match_result(exam_id, examiner_id);
CREATE INDEX idx_match_exam_srcsub  ON match_result(exam_id, src_submission_id);
CREATE INDEX idx_match_exam_tgtsub  ON match_result(exam_id, tgt_submission_id);
CREATE INDEX idx_match_score_desc   ON match_result(exam_id, score DESC);

CREATE TABLE duplicate_detection (
  id                 bigserial PRIMARY KEY,
  exam_id            uuid NOT NULL REFERENCES exam(exam_id),
  examiner_id        uuid REFERENCES examiner(examiner_id),
  submission_id1     uuid NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
  submission_id2     uuid NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
  student_id1        uuid NOT NULL,
  student_id2        uuid NOT NULL,
  vector_score       numeric(6,5),           -- ví dụ: max/avg top-k cosine giữa 2 submissions
  is_duplicate       boolean NOT NULL DEFAULT false,
  threshold_used     text,                   -- ví dụ: "cosine>=0.90 with k=5, min_hits=3"
  matched_units      jsonb,                  -- danh sách cặp unit khớp (src_unit_id, tgt_unit_id, score)
  notes              text,
  created_at         timestamptz DEFAULT now(),
  CONSTRAINT chk_dup_pair CHECK (submission_id1 <> submission_id2)
);

CREATE UNIQUE INDEX uq_dup_pair
  ON duplicate_detection(exam_id, examiner_id, submission_id1, submission_id2);

CREATE INDEX idx_dup_vector_desc
  ON duplicate_detection(exam_id, examiner_id, vector_score DESC);

-- ============================================================
-- 5) WHITELIST THEO UNIT_KEY (KHÔNG DÙNG HASH)
-- ============================================================
CREATE TABLE whitelist_unit (
  id         bigserial PRIMARY KEY,
  exam_id    uuid NOT NULL REFERENCES exam(exam_id),
  unit_key   text NOT NULL,                 -- khớp với code_unit.unit_key
  note       text,
  created_at timestamptz DEFAULT now(),
  UNIQUE (exam_id, unit_key)
);

-- ============================================================
-- HOÀN TẤT
-- ============================================================
-- Sau khi load dữ liệu ban đầu, nên chạy:
--   ANALYZE code_embedding;
-- để chỉ số ivfflat hoạt động tối ưu.
