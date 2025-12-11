-- KẾT NỐI VÀO DB: prn232_worker_db
-- ================================================================
-- 1. CLEANUP (XÓA BẢNG CŨ NẾU CÓ)
-- ================================================================
DROP TABLE IF EXISTS match_result CASCADE;
DROP TABLE IF EXISTS duplicate_detection CASCADE;
DROP TABLE IF EXISTS code_embedding CASCADE;
DROP TABLE IF EXISTS code_unit CASCADE;
DROP TABLE IF EXISTS code_file CASCADE;
DROP TABLE IF EXISTS submission CASCADE;
DROP TABLE IF EXISTS examiner CASCADE;
DROP TABLE IF EXISTS exam CASCADE;

-- ================================================================
-- 2. EXTENSIONS (CHỈ CẦN UUID, BỎ VECTOR)
-- ================================================================
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
-- BỎ DÒNG: CREATE EXTENSION vector; -> Để tránh lỗi bạn đang gặp

-- ================================================================
-- 3. SCHEMA
-- ================================================================
CREATE TABLE exam (
    exam_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    code TEXT UNIQUE NOT NULL,
    title TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE examiner (
    examiner_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    code TEXT UNIQUE NOT NULL,
    name TEXT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE submission (
    submission_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    exam_id UUID NOT NULL REFERENCES exam(exam_id),
    student_id UUID NOT NULL, 
    examiner_id UUID REFERENCES examiner(examiner_id),
    storage_key TEXT NOT NULL,
    status TEXT DEFAULT 'processed',
    total_files INT DEFAULT 0,
    total_lines INT DEFAULT 0,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    analyzed_at TIMESTAMPTZ
);

CREATE TABLE code_file (
    file_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    submission_id UUID NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
    rel_path TEXT NOT NULL,
    language TEXT,
    line_count INT,
    file_hash BYTEA,
    file_size BIGINT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE code_unit (
    unit_id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    submission_id UUID NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
    file_id UUID NOT NULL REFERENCES code_file(file_id) ON DELETE CASCADE,
    unit_kind TEXT NOT NULL,
    unit_key TEXT NOT NULL,
    start_line INT,
    end_line INT,
    content TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- BẢNG QUAN TRỌNG NHẤT ĐÃ ĐƯỢC SỬA ĐỔI
CREATE TABLE code_embedding (
    unit_id UUID PRIMARY KEY REFERENCES code_unit(unit_id) ON DELETE CASCADE,
    submission_id UUID NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
    
    -- SỬA ĐỔI: Dùng TEXT để lưu chuỗi JSON thay vì kiểu VECTOR
    emb TEXT NOT NULL, 
    
    model_name TEXT NOT NULL,
    embedding_dimension INT NOT NULL,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE duplicate_detection (
    id BIGSERIAL PRIMARY KEY,
    exam_id UUID NOT NULL REFERENCES exam(exam_id),
    examiner_id UUID REFERENCES examiner(examiner_id),
    submission_id1 UUID NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
    submission_id2 UUID NOT NULL REFERENCES submission(submission_id) ON DELETE CASCADE,
    student_id1 UUID NOT NULL,
    student_id2 UUID NOT NULL,
    vector_score NUMERIC(6,5),
    is_duplicate BOOLEAN DEFAULT FALSE,
    threshold_used TEXT,
    matched_units JSONB,
    notes TEXT,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    CONSTRAINT chk_diff CHECK (submission_id1 <> submission_id2)
);

CREATE TABLE match_result (
    id BIGSERIAL PRIMARY KEY,
    exam_id UUID NOT NULL REFERENCES exam(exam_id),
    src_unit_id UUID NOT NULL REFERENCES code_unit(unit_id),
    tgt_unit_id UUID NOT NULL REFERENCES code_unit(unit_id),
    score NUMERIC(6,5),
    detail JSONB,
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- ================================================================
-- 4. SEED DATA (DỮ LIỆU MẪU)
-- ================================================================
INSERT INTO exam (code, title) VALUES ('PRN232_FINAL', 'Final Exam');
INSERT INTO examiner (code, name) VALUES ('WORKER_01', 'AI Worker Node');